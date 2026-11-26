using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PadForge.Tools.OutputFamilyProbe
{
    /// <summary>The Windows calls the probe makes: SetupAPI over the HID
    /// interface class, the HID parser, kernel32's overlapped read, XInput and
    /// Raw Input.</summary>
    internal static unsafe class Native
    {
        // ── SetupAPI ───────────────────────────────────────────────────

        internal const int DIGCF_PRESENT = 0x02;
        internal const int DIGCF_DEVICEINTERFACE = 0x10;

        [StructLayout(LayoutKind.Sequential)]
        internal struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct SP_DEVINFO_DATA
        {
            public int cbSize;
            public Guid ClassGuid;
            public int DevInst;
            public IntPtr Reserved;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        internal static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid interfaceGuid,
            int index, ref SP_DEVICE_INTERFACE_DATA data);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data,
            IntPtr detail, int detailSize, out int requiredSize, ref SP_DEVINFO_DATA devInfo);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref SP_DEVINFO_DATA devInfo,
            char[] id, int idSize, out int requiredSize);

        [DllImport("setupapi.dll", SetLastError = true)]
        internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        // ── HID ────────────────────────────────────────────────────────

        [DllImport("hid.dll")]
        internal static extern void HidD_GetHidGuid(out Guid guid);

        [StructLayout(LayoutKind.Sequential)]
        internal struct HIDD_ATTRIBUTES
        {
            public int Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        [DllImport("hid.dll", SetLastError = true)]
        internal static extern bool HidD_GetAttributes(SafeFileHandle device, ref HIDD_ATTRIBUTES attributes);

        [DllImport("hid.dll", SetLastError = true)]
        internal static extern bool HidD_GetPreparsedData(SafeFileHandle device, out IntPtr preparsed);

        [DllImport("hid.dll")]
        internal static extern bool HidD_FreePreparsedData(IntPtr preparsed);

        [DllImport("hid.dll", SetLastError = true)]
        internal static extern bool HidD_GetFeature(SafeFileHandle device, byte[] buffer, int length);

        [StructLayout(LayoutKind.Sequential)]
        internal struct HIDP_CAPS
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        internal const int HIDP_STATUS_SUCCESS = 0x00110000;
        internal const int HidP_Input = 0;

        [DllImport("hid.dll")]
        internal static extern int HidP_GetCaps(IntPtr preparsed, ref HIDP_CAPS caps);

        /// <summary>HIDP_VALUE_CAPS, 72 bytes on x64. Only the fields the
        /// probe reads are named. The union's range form and its not-range
        /// form share their first usage word.</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct HIDP_VALUE_CAPS
        {
            public ushort UsagePage;
            public byte ReportID;
            public byte IsAlias;
            public ushort BitField;
            public ushort LinkCollection;
            public ushort LinkUsage;
            public ushort LinkUsagePage;
            public byte IsRange;
            public byte IsStringRange;
            public byte IsDesignatorRange;
            public byte IsAbsolute;
            public byte HasNull;
            public byte Reserved;
            public ushort BitSize;
            public ushort ReportCount;
            public ushort Reserved2_0, Reserved2_1, Reserved2_2, Reserved2_3, Reserved2_4;
            public uint UnitsExp;
            public uint Units;
            public int LogicalMin;
            public int LogicalMax;
            public int PhysicalMin;
            public int PhysicalMax;
            public ushort UsageMin;
            public ushort UsageMax;
            public ushort StringMin, StringMax;
            public ushort DesignatorMin, DesignatorMax;
            public ushort DataIndexMin, DataIndexMax;
        }

        [DllImport("hid.dll")]
        internal static extern int HidP_GetValueCaps(int reportType, [Out] HIDP_VALUE_CAPS[] caps,
            ref ushort length, IntPtr preparsed);

        [DllImport("hid.dll")]
        internal static extern int HidP_GetUsageValue(int reportType, ushort usagePage, ushort linkCollection,
            ushort usage, out uint value, IntPtr preparsed, byte[] report, int reportLength);

        [DllImport("hid.dll")]
        internal static extern int HidP_GetUsages(int reportType, ushort usagePage, ushort linkCollection,
            [Out] ushort[] usages, ref int usageLength, IntPtr preparsed, byte[] report, int reportLength);

        // ── kernel32 ───────────────────────────────────────────────────

        internal const uint GENERIC_READ = 0x80000000;
        internal const uint GENERIC_WRITE = 0x40000000;
        internal const uint FILE_SHARE_READ = 0x1;
        internal const uint FILE_SHARE_WRITE = 0x2;
        internal const uint OPEN_EXISTING = 3;
        internal const uint FILE_FLAG_OVERLAPPED = 0x40000000;
        internal const int ERROR_IO_PENDING = 997;
        internal const uint WAIT_OBJECT_0 = 0;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
            uint disposition, uint flags, IntPtr template);

        [StructLayout(LayoutKind.Sequential)]
        internal struct OVERLAPPED
        {
            public IntPtr Internal;
            public IntPtr InternalHigh;
            public uint Offset;
            public uint OffsetHigh;
            public IntPtr hEvent;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool ReadFile(SafeFileHandle file, byte* buffer, int count, IntPtr read, OVERLAPPED* overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool GetOverlappedResult(SafeFileHandle file, OVERLAPPED* overlapped, out int transferred, bool wait);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool CancelIoEx(SafeFileHandle file, OVERLAPPED* overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr CreateEventW(IntPtr security, bool manualReset, bool initialState, IntPtr name);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool ResetEvent(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool CloseHandle(IntPtr handle);

        internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool QueryFullProcessImageNameW(IntPtr process, int flags, char[] name, ref int size);

        // ── XInput (HIDMaestro test/Latency.cs:35-49) ──────────────────

        [StructLayout(LayoutKind.Sequential)]
        internal struct XINPUT_GAMEPAD
        {
            public ushort wButtons;
            public byte bLeftTrigger, bRightTrigger;
            public short sThumbLX, sThumbLY, sThumbRX, sThumbRY;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct XINPUT_STATE
        {
            public uint dwPacketNumber;
            public XINPUT_GAMEPAD Gamepad;
        }

        /// <summary>Windows' own xinput1_4.dll, the one a game loads. PadForge.App
        /// puts its OpenXInput fork, which skips every HIDMaestro controller
        /// when it assigns user indexes, beside the probe as it puts it beside
        /// PadForge.exe (PadForge.App.csproj, the comment above its
        /// xinput1_4.dll content item), and a load by name takes that copy
        /// first. A load by full path takes the file named, beside any copy of
        /// the same name.</summary>
        internal static readonly string XInputPath = System.IO.Path.Combine(Environment.SystemDirectory, "xinput1_4.dll");

        private static IntPtr s_xinputModule;
        private static delegate* unmanaged<uint, XINPUT_STATE*, uint> s_xinputGetState;

        /// <summary>The module <see cref="XInputGetState"/> calls into.</summary>
        internal static IntPtr XInputModule
        {
            get
            {
                if (s_xinputModule == IntPtr.Zero)
                {
                    IntPtr module = NativeLibrary.Load(XInputPath);
                    s_xinputGetState = (delegate* unmanaged<uint, XINPUT_STATE*, uint>)NativeLibrary.GetExport(module, "XInputGetState");
                    s_xinputModule = module;
                }
                return s_xinputModule;
            }
        }

        internal static uint XInputGetState(uint userIndex, out XINPUT_STATE state)
        {
            _ = XInputModule;
            state = default;
            fixed (XINPUT_STATE* p = &state) return s_xinputGetState(userIndex, p);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern int GetModuleFileNameW(IntPtr module, char[] name, int size);

        /// <summary>The file a loaded module came from.</summary>
        internal static string ModulePath(IntPtr module)
        {
            var buf = new char[1024];
            int n = GetModuleFileNameW(module, buf, buf.Length);
            return n > 0 ? new string(buf, 0, n) : "(unknown)";
        }

        // ── Device tree and WinUSB (HIDMaestro test/probes/
        //    switch2_composite_check/Live.cs:676-729) ──────────────────

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        internal static extern int CM_Locate_DevNodeW(out uint node, string id, uint flags);
        [DllImport("cfgmgr32.dll")]
        internal static extern int CM_Get_Parent(out uint parent, uint node, uint flags);
        [DllImport("cfgmgr32.dll")]
        internal static extern int CM_Get_Child(out uint child, uint node, uint flags);
        [DllImport("cfgmgr32.dll")]
        internal static extern int CM_Get_Sibling(out uint sibling, uint node, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        internal static extern int CM_Get_Device_IDW(uint node, [Out] char[] buffer, int length, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        internal static extern int CM_Get_Device_Interface_List_SizeW(out int length, ref Guid guid, string deviceId, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        internal static extern int CM_Get_Device_Interface_ListW(ref Guid guid, string deviceId, [Out] char[] buffer, int length, uint flags);

        [DllImport("winusb.dll", SetLastError = true)]
        internal static extern bool WinUsb_Initialize(SafeFileHandle device, out IntPtr handle);
        [DllImport("winusb.dll", SetLastError = true)]
        internal static extern bool WinUsb_Free(IntPtr handle);
        [DllImport("winusb.dll", SetLastError = true)]
        internal static extern bool WinUsb_SetPipePolicy(IntPtr handle, byte pipe, uint policy, uint length, ref uint value);
        [DllImport("winusb.dll", SetLastError = true)]
        internal static extern bool WinUsb_WritePipe(IntPtr handle, byte pipe, byte[] buffer, uint length, out uint transferred, IntPtr overlapped);
        [DllImport("winusb.dll", SetLastError = true)]
        internal static extern bool WinUsb_ReadPipe(IntPtr handle, byte pipe, [Out] byte[] buffer, uint length, out uint transferred, IntPtr overlapped);

        internal const uint PIPE_TRANSFER_TIMEOUT = 0x03;

        // ── Raw Input ──────────────────────────────────────────────────

        internal const int WM_INPUT = 0x00FF;
        internal const int WM_CLOSE = 0x0010;
        internal const int WM_DESTROY = 0x0002;
        internal const int WM_APP = 0x8000;
        internal const int RIDEV_INPUTSINK = 0x00000100;
        internal const int RID_INPUT = 0x10000003;
        internal const int RIDI_DEVICENAME = 0x20000007;
        internal const int RIM_TYPEMOUSE = 0;
        internal const int RIM_TYPEKEYBOARD = 1;
        internal static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);

        [StructLayout(LayoutKind.Sequential)]
        internal struct RAWINPUTDEVICE
        {
            public ushort usUsagePage;
            public ushort usUsage;
            public int dwFlags;
            public IntPtr hwndTarget;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct RAWINPUTHEADER
        {
            public int dwType;
            public int dwSize;
            public IntPtr hDevice;
            public IntPtr wParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct RAWMOUSE
        {
            public ushort usFlags;
            // The button union is a ULONG, so it starts at offset 4.
            private ushort _pad;
            public ushort usButtonFlags;
            public ushort usButtonData;
            public uint ulRawButtons;
            public int lLastX;
            public int lLastY;
            public uint ulExtraInformation;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct RAWKEYBOARD
        {
            public ushort MakeCode;
            public ushort Flags;
            public ushort Reserved;
            public ushort VKey;
            public uint Message;
            public uint ExtraInformation;
        }

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool RegisterRawInputDevices([In] RAWINPUTDEVICE[] devices, int count, int size);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern int GetRawInputData(IntPtr hRawInput, int command, byte* data, ref int size, int headerSize);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern int GetRawInputDeviceInfoW(IntPtr device, int command, char[] data, ref int size);

        internal delegate IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WNDCLASSEXW
        {
            public int cbSize;
            public int style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MSG
        {
            public IntPtr hwnd;
            public int message;
            public IntPtr wParam;
            public IntPtr lParam;
            public int time;
            public int ptX, ptY;
            public int lPrivate;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern ushort RegisterClassExW(ref WNDCLASSEXW cls);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool UnregisterClassW(string className, IntPtr instance);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr CreateWindowExW(int exStyle, string className, string windowName, int style,
            int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool DestroyWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        internal static extern IntPtr DefWindowProcW(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        internal static extern int GetMessageW(out MSG msg, IntPtr hwnd, int min, int max);

        [DllImport("user32.dll")]
        internal static extern bool TranslateMessage(ref MSG msg);

        [DllImport("user32.dll")]
        internal static extern IntPtr DispatchMessageW(ref MSG msg);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool PostMessageW(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        internal static extern void PostQuitMessage(int code);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr GetModuleHandleW(string name);
    }
}
