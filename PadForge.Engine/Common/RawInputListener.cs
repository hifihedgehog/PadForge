using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace PadForge.Engine
{
    /// <summary>
    /// Receives keyboard and mouse input via Windows Raw Input API, even when
    /// the application window is not focused (RIDEV_INPUTSINK). Creates a hidden
    /// message-only window on a dedicated background thread.
    ///
    /// State is tracked per-device via <see cref="RAWINPUT.header.hDevice"/>,
    /// so arcade encoders and other multi-device setups get isolated input.
    /// </summary>
    public static class RawInputListener
    {
        // ─────────────────────────────────────────────
        //  Constants
        // ─────────────────────────────────────────────

        private const int WM_INPUT = 0x00FF;
        private const int WM_QUIT = 0x0012;

        private const ushort HID_USAGE_PAGE_GENERIC = 0x01;
        private const ushort HID_USAGE_GENERIC_MOUSE = 0x02;
        private const ushort HID_USAGE_GENERIC_KEYBOARD = 0x06;

        private const uint RIDEV_INPUTSINK = 0x00000100;
        private const uint RID_INPUT = 0x10000003;

        private const uint RIM_TYPEMOUSE = 0;
        private const uint RIM_TYPEKEYBOARD = 1;
        private const uint RIM_TYPEHID = 2;

        private const ushort RI_KEY_BREAK = 0x0001;

        private const ushort RI_MOUSE_LEFT_BUTTON_DOWN = 0x0001;
        private const ushort RI_MOUSE_LEFT_BUTTON_UP = 0x0002;
        private const ushort RI_MOUSE_RIGHT_BUTTON_DOWN = 0x0004;
        private const ushort RI_MOUSE_RIGHT_BUTTON_UP = 0x0008;
        private const ushort RI_MOUSE_MIDDLE_BUTTON_DOWN = 0x0010;
        private const ushort RI_MOUSE_MIDDLE_BUTTON_UP = 0x0020;
        private const ushort RI_MOUSE_BUTTON_4_DOWN = 0x0040;
        private const ushort RI_MOUSE_BUTTON_4_UP = 0x0080;
        private const ushort RI_MOUSE_BUTTON_5_DOWN = 0x0100;
        private const ushort RI_MOUSE_BUTTON_5_UP = 0x0200;

        private static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);

        // GetRawInputDeviceInfo commands
        private const uint RIDI_DEVICENAME = 0x20000007;
        private const uint RIDI_DEVICEINFO = 0x2000000b;

        // ─────────────────────────────────────────────
        //  P/Invoke Structs
        // ─────────────────────────────────────────────

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTDEVICE
        {
            public ushort usUsagePage;
            public ushort usUsage;
            public uint dwFlags;
            public IntPtr hwndTarget;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTHEADER
        {
            public uint dwType;
            public uint dwSize;
            public IntPtr hDevice;
            public IntPtr wParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWKEYBOARD
        {
            public ushort MakeCode;
            public ushort Flags;
            public ushort Reserved;
            public ushort VKey;
            public uint Message;
            public uint ExtraInformation;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWMOUSE
        {
            public ushort usFlags;
            public ushort usButtonFlags;
            public ushort usButtonData;
            public uint ulRawButtons;
            public int lLastX;
            public int lLastY;
            public uint ulExtraInformation;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WNDCLASSEXW
        {
            public uint cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public IntPtr lpszMenuName;
            public IntPtr lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int pt_x;
            public int pt_y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTDEVICELIST
        {
            public IntPtr hDevice;
            public uint dwType;
        }

        // RID_DEVICE_INFO — full union layout for reading VID/PID from the API.
        // Explicit layout overlays keyboard, mouse, and HID sub-structs at offset 8.
        [StructLayout(LayoutKind.Explicit)]
        private struct RID_DEVICE_INFO
        {
            [FieldOffset(0)] public uint cbSize;
            [FieldOffset(4)] public uint dwType;
            [FieldOffset(8)] public RID_DEVICE_INFO_MOUSE mouse;
            [FieldOffset(8)] public RID_DEVICE_INFO_KEYBOARD keyboard;
            [FieldOffset(8)] public RID_DEVICE_INFO_HID hid;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RID_DEVICE_INFO_MOUSE
        {
            public uint dwId;
            public uint dwNumberOfButtons;
            public uint dwSampleRate;
            public int fHasHorizontalWheel;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RID_DEVICE_INFO_KEYBOARD
        {
            public uint dwType;
            public uint dwSubType;
            public uint dwKeyboardMode;
            public uint dwNumberOfFunctionKeys;
            public uint dwNumberOfIndicators;
            public uint dwNumberOfKeysTotal;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RID_DEVICE_INFO_HID
        {
            public uint dwVendorId;
            public uint dwProductId;
            public uint dwVersionNumber;
            public ushort usUsagePage;
            public ushort usUsage;
        }

        // ─────────────────────────────────────────────
        //  P/Invoke Functions
        // ─────────────────────────────────────────────

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterRawInputDevices(
            RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetRawInputData(
            IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetRawInputDeviceList(
            [Out] RAWINPUTDEVICELIST[] pRawInputDeviceList, ref uint puiNumDevices, uint cbSize);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint GetRawInputDeviceInfoW(
            IntPtr hDevice, uint uiCommand, IntPtr pData, ref uint pcbSize);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowExW(
            uint dwExStyle, IntPtr lpClassName, string lpWindowName,
            uint dwStyle, int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpWndClass);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessageW(ref MSG lpMsg);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(string lpModuleName);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFile(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition,
            uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("hid.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool HidD_GetProductString(
            IntPtr hidDeviceObject, byte[] buffer, uint bufferLength);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetAttributes(
            IntPtr hidDeviceObject, ref HIDD_ATTRIBUTES attributes);

        [StructLayout(LayoutKind.Sequential)]
        private struct HIDD_ATTRIBUTES
        {
            public uint Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        // ─────────────────────────────────────────────
        //  Public Device Info
        // ─────────────────────────────────────────────

        /// <summary>
        /// Describes a Raw Input device (keyboard or mouse).
        /// </summary>
        public struct DeviceInfo
        {
            /// <summary>Raw Input device handle — stable for the device's connection lifetime.</summary>
            public IntPtr Handle;

            /// <summary>Human-readable name extracted from the device interface path.</summary>
            public string Name;

            /// <summary>Device interface path (e.g. \\?\HID#VID_1234&amp;PID_5678#...).</summary>
            public string DevicePath;

            /// <summary>Device type: 0 = mouse, 1 = keyboard.</summary>
            public uint Type;

            /// <summary>USB Vendor ID (from RIDI_DEVICEINFO → HID sub-struct). 0 if unavailable.</summary>
            public ushort VendorId;

            /// <summary>USB Product ID (from RIDI_DEVICEINFO → HID sub-struct). 0 if unavailable.</summary>
            public ushort ProductId;
        }

        // ─────────────────────────────────────────────
        //  Per-Device State
        // ─────────────────────────────────────────────

        /// <summary>Per-keyboard key state arrays, keyed by hDevice.</summary>
        private static readonly ConcurrentDictionary<IntPtr, bool[]> _keyboardStates = new();

        /// <summary>Per-mouse state, keyed by hDevice.</summary>
        private static readonly ConcurrentDictionary<IntPtr, MouseDeviceState> _mouseStates = new();

        private class MouseDeviceState
        {
            public long DeltaX;
            public long DeltaY;
            public readonly bool[] Buttons = new bool[5];
        }

        /// <summary>Aggregate mouse state — accumulates from all mice independently.</summary>
        private static readonly MouseDeviceState _aggregateMouseState = new();

        private static volatile bool _running;
        private static IntPtr _hwnd;
        private static Thread _thread;

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        private static WndProcDelegate _wndProcDelegate;

        // ─────────────────────────────────────────────
        //  Public API — Lifecycle
        // ─────────────────────────────────────────────

        public static void Start()
        {
            if (_running) return;
            _running = true;

            _thread = new Thread(MessagePumpThread)
            {
                Name = "PadForge.RawInputListener",
                IsBackground = true
            };
            _thread.Start();
        }

        public static void Stop()
        {
            if (!_running) return;
            _running = false;

            if (_hwnd != IntPtr.Zero)
                PostMessageW(_hwnd, WM_QUIT, IntPtr.Zero, IntPtr.Zero);

            _thread?.Join(TimeSpan.FromSeconds(2));
            _thread = null;

            _keyboardStates.Clear();
            _mouseStates.Clear();

            // Reset aggregate mouse state.
            Interlocked.Exchange(ref _aggregateMouseState.DeltaX, 0);
            Interlocked.Exchange(ref _aggregateMouseState.DeltaY, 0);
            Array.Clear(_aggregateMouseState.Buttons, 0, _aggregateMouseState.Buttons.Length);
        }

        // ─────────────────────────────────────────────
        //  Public API — Device Enumeration
        // ─────────────────────────────────────────────

        /// <summary>
        /// Enumerates all currently connected Raw Input keyboard devices.
        /// Includes a synthetic "All Keyboards" aggregate device if multiple keyboards exist.
        /// </summary>
        public static DeviceInfo[] EnumerateKeyboards()
        {
            var devices = EnumerateDevicesByType(RIM_TYPEKEYBOARD);
            if (devices.Length < 2) return devices;

            // Prepend the aggregate device.
            var result = new DeviceInfo[devices.Length + 1];
            result[0] = new DeviceInfo
            {
                Handle = AggregateKeyboardHandle,
                Name = "All Keyboards (Merged)",
                DevicePath = "aggregate://keyboards",
                Type = RIM_TYPEKEYBOARD
            };
            Array.Copy(devices, 0, result, 1, devices.Length);
            return result;
        }

        /// <summary>
        /// Enumerates all currently connected Raw Input mouse devices.
        /// Includes a synthetic "All Mice" aggregate device if multiple mice exist.
        /// </summary>
        public static DeviceInfo[] EnumerateMice()
        {
            var devices = EnumerateDevicesByType(RIM_TYPEMOUSE);
            if (devices.Length < 2) return devices;

            // Prepend the aggregate device.
            var result = new DeviceInfo[devices.Length + 1];
            result[0] = new DeviceInfo
            {
                Handle = AggregateMouseHandle,
                Name = "All Mice (Merged)",
                DevicePath = "aggregate://mice",
                Type = RIM_TYPEMOUSE
            };
            Array.Copy(devices, 0, result, 1, devices.Length);
            return result;
        }

        private static DeviceInfo[] EnumerateDevicesByType(uint targetType)
        {
            uint numDevices = 0;
            uint structSize = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();

            if (GetRawInputDeviceList(null, ref numDevices, structSize) != 0)
                return Array.Empty<DeviceInfo>();

            if (numDevices == 0)
                return Array.Empty<DeviceInfo>();

            var deviceList = new RAWINPUTDEVICELIST[numDevices];
            uint result = GetRawInputDeviceList(deviceList, ref numDevices, structSize);
            if (result == unchecked((uint)-1))
                return Array.Empty<DeviceInfo>();

            var matches = new List<DeviceInfo>();
            for (int i = 0; i < (int)result; i++)
            {
                if (deviceList[i].dwType != targetType)
                    continue;

                IntPtr hDevice = deviceList[i].hDevice;
                string devicePath = GetDeviceName(hDevice);
                string friendlyName = ExtractFriendlyName(devicePath, targetType);

                GetDeviceVidPid(hDevice, devicePath, out ushort vid, out ushort pid);

                matches.Add(new DeviceInfo
                {
                    Handle = hDevice,
                    Name = friendlyName,
                    DevicePath = devicePath ?? string.Empty,
                    Type = targetType,
                    VendorId = vid,
                    ProductId = pid
                });
            }

            return matches.ToArray();
        }

        private static string GetDeviceName(IntPtr hDevice)
        {
            uint size = 0;
            GetRawInputDeviceInfoW(hDevice, RIDI_DEVICENAME, IntPtr.Zero, ref size);
            if (size == 0) return null;

            IntPtr buffer = Marshal.AllocHGlobal((int)size * 2); // chars are 2 bytes
            try
            {
                uint written = GetRawInputDeviceInfoW(hDevice, RIDI_DEVICENAME, buffer, ref size);
                if (written == unchecked((uint)-1) || written == 0)
                    return null;

                return Marshal.PtrToStringUni(buffer);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>
        /// Gets VID/PID for a Raw Input device. Tries three methods:
        ///   1. HidD_GetAttributes — opens the device path and reads HID attributes (most reliable for USB HID)
        ///   2. RIDI_DEVICEINFO — uses the Raw Input API to get the HID sub-struct
        ///   3. Device path parsing — extracts VID_xxxx&amp;PID_xxxx from the path string
        /// </summary>
        private static void GetDeviceVidPid(IntPtr hDevice, string devicePath, out ushort vendorId, out ushort productId)
        {
            vendorId = 0;
            productId = 0;

            // Method 1: HidD_GetAttributes — open device path, query HID driver directly.
            if (!string.IsNullOrEmpty(devicePath) && !devicePath.StartsWith("aggregate://"))
            {
                try
                {
                    IntPtr handle = CreateFile(
                        devicePath,
                        0, // No access rights needed for HidD_GetAttributes
                        3, // FILE_SHARE_READ | FILE_SHARE_WRITE
                        IntPtr.Zero,
                        3, // OPEN_EXISTING
                        0,
                        IntPtr.Zero);

                    if (handle != IntPtr.Zero && handle != INVALID_HANDLE_VALUE)
                    {
                        try
                        {
                            var attrs = new HIDD_ATTRIBUTES();
                            attrs.Size = (uint)Marshal.SizeOf<HIDD_ATTRIBUTES>();
                            if (HidD_GetAttributes(handle, ref attrs))
                            {
                                vendorId = attrs.VendorID;
                                productId = attrs.ProductID;
                                return;
                            }
                        }
                        finally
                        {
                            CloseHandle(handle);
                        }
                    }
                }
                catch { /* non-fatal — device may not be HID */ }
            }

            // Method 2: RIDI_DEVICEINFO — works if the device is reported as HID type.
            try
            {
                uint size = (uint)Marshal.SizeOf<RID_DEVICE_INFO>();
                IntPtr buffer = Marshal.AllocHGlobal((int)size);
                try
                {
                    Marshal.WriteInt32(buffer, (int)size); // cbSize
                    uint written = GetRawInputDeviceInfoW(hDevice, RIDI_DEVICEINFO, buffer, ref size);
                    if (written != unchecked((uint)-1) && written > 0)
                    {
                        var info = Marshal.PtrToStructure<RID_DEVICE_INFO>(buffer);
                        if (info.dwType == RIM_TYPEHID)
                        {
                            vendorId = (ushort)info.hid.dwVendorId;
                            productId = (ushort)info.hid.dwProductId;
                            return;
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            catch { /* non-fatal */ }

            // Method 3: Parse VID_xxxx&PID_xxxx from device path string.
            if (string.IsNullOrEmpty(devicePath)) return;
            string upper = devicePath.ToUpperInvariant();

            int vidIdx = upper.IndexOf("VID_");
            if (vidIdx >= 0 && vidIdx + 8 <= upper.Length &&
                ushort.TryParse(upper.Substring(vidIdx + 4, 4),
                    System.Globalization.NumberStyles.HexNumber, null, out ushort vid))
                vendorId = vid;

            int pidIdx = upper.IndexOf("PID_");
            if (pidIdx >= 0 && pidIdx + 8 <= upper.Length &&
                ushort.TryParse(upper.Substring(pidIdx + 4, 4),
                    System.Globalization.NumberStyles.HexNumber, null, out ushort pid))
                productId = pid;
        }

        /// <summary>
        /// Extracts a human-readable name from a Raw Input device path.
        /// Fallback chain:
        ///   1. HidD_GetProductString (USB HID devices)
        ///   2. Windows registry — USB parent device FriendlyName/DeviceDesc
        ///   3. Windows registry — device's own FriendlyName/DeviceDesc
        ///   4. VID:PID label
        ///   5. Generic "Keyboard" / "Mouse"
        /// </summary>
        private static string ExtractFriendlyName(string devicePath, uint type)
        {
            string fallback = type == RIM_TYPEKEYBOARD ? "Keyboard" : "Mouse";
            if (string.IsNullOrEmpty(devicePath)) return fallback;

            // 1. HID product string (works for some USB HID devices).
            string hidName = TryGetHidProductString(devicePath);
            if (hidName != null)
                return hidName;

            // 2. Registry: USB parent device name.
            //    HID child devices have generic names ("HID Keyboard Device"),
            //    but the USB parent has the actual product name.
            string regName = TryGetUsbParentDeviceName(devicePath);
            if (regName != null)
                return regName;

            // 3. Registry: device's own entry (PS/2, RDP, etc.)
            string selfName = TryGetRegistryDeviceName(devicePath);
            if (selfName != null)
                return selfName;

            // 4. VID:PID fallback.
            string upper = devicePath.ToUpperInvariant();
            string vid = null, pid = null;

            int vidIdx = upper.IndexOf("VID_");
            if (vidIdx >= 0 && vidIdx + 8 <= upper.Length)
                vid = upper.Substring(vidIdx + 4, 4);

            int pidIdx = upper.IndexOf("PID_");
            if (pidIdx >= 0 && pidIdx + 8 <= upper.Length)
                pid = upper.Substring(pidIdx + 4, 4);

            if (vid != null && pid != null)
                return $"{fallback} ({vid}:{pid})";

            return fallback;
        }

        /// <summary>
        /// Converts a Raw Input device path to a device instance ID and reads
        /// the device's own FriendlyName or DeviceDesc from the registry.
        /// Works for all device types (PS/2, RDP, virtual, etc.).
        /// </summary>
        private static string TryGetRegistryDeviceName(string devicePath)
        {
            string instanceId = DevicePathToInstanceId(devicePath);
            if (instanceId == null) return null;

            return ReadRegistryDeviceName($@"SYSTEM\CurrentControlSet\Enum\{instanceId}");
        }

        /// <summary>
        /// For HID devices (\\?\HID#VID_xxxx&amp;PID_xxxx#...), looks up the USB parent
        /// device (USB\VID_xxxx&amp;PID_xxxx\*) which typically has the actual product name
        /// (e.g., "Corsair K70 RGB" instead of "HID Keyboard Device").
        /// </summary>
        private static string TryGetUsbParentDeviceName(string devicePath)
        {
            try
            {
                string upper = devicePath.ToUpperInvariant();
                if (!upper.Contains("VID_") || !upper.Contains("PID_"))
                    return null;

                int vidIdx = upper.IndexOf("VID_");
                int pidIdx = upper.IndexOf("PID_");
                if (vidIdx < 0 || pidIdx < 0 || vidIdx + 8 > upper.Length || pidIdx + 8 > upper.Length)
                    return null;

                string vid = upper.Substring(vidIdx + 4, 4);
                string pid = upper.Substring(pidIdx + 4, 4);

                // Look up USB parent: HKLM\SYSTEM\CurrentControlSet\Enum\USB\VID_xxxx&PID_xxxx
                string usbKey = $@"SYSTEM\CurrentControlSet\Enum\USB\VID_{vid}&PID_{pid}";
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(usbKey);
                if (key == null) return null;

                // Iterate serial number subkeys to find a name.
                foreach (string subkeyName in key.GetSubKeyNames())
                {
                    string name = ReadRegistryDeviceName($@"{usbKey}\{subkeyName}");
                    if (name != null && !IsGenericDeviceName(name))
                        return name;
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Reads FriendlyName or DeviceDesc from a registry key under HKLM.
        /// DeviceDesc format: "@input.inf,%string%;Actual Device Name"
        /// — the part after the last semicolon is the human-readable name.
        /// </summary>
        private static string ReadRegistryDeviceName(string regPath)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(regPath);
                if (key == null) return null;

                // Prefer FriendlyName (more specific when present).
                string friendly = key.GetValue("FriendlyName") as string;
                if (!string.IsNullOrWhiteSpace(friendly))
                {
                    string cleaned = CleanDeviceDesc(friendly);
                    if (!string.IsNullOrWhiteSpace(cleaned))
                        return cleaned;
                }

                // Fall back to DeviceDesc.
                string desc = key.GetValue("DeviceDesc") as string;
                if (!string.IsNullOrWhiteSpace(desc))
                {
                    string cleaned = CleanDeviceDesc(desc);
                    if (!string.IsNullOrWhiteSpace(cleaned))
                        return cleaned;
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Converts a Raw Input device path to a PnP device instance ID.
        /// Example: \\?\HID#VID_046D&amp;PID_C52B#7&amp;1234&amp;0&amp;0000#{guid}
        ///       → HID\VID_046D&amp;PID_C52B\7&amp;1234&amp;0&amp;0000
        /// </summary>
        private static string DevicePathToInstanceId(string devicePath)
        {
            if (string.IsNullOrEmpty(devicePath))
                return null;

            string path = devicePath;

            // Strip \\?\ prefix.
            if (path.StartsWith(@"\\?\"))
                path = path.Substring(4);

            // Remove device interface GUID suffix ({...}).
            int guidIdx = path.LastIndexOf('{');
            if (guidIdx > 0)
                path = path.Substring(0, guidIdx);

            // Replace # with \ (device path uses # as separator).
            path = path.Replace('#', '\\');
            path = path.TrimEnd('\\');

            return string.IsNullOrEmpty(path) ? null : path;
        }

        /// <summary>
        /// Cleans a DeviceDesc or FriendlyName registry value.
        /// Format: "@driver.inf,%string_token%;Human Readable Name"
        /// Returns the part after the last semicolon.
        /// </summary>
        private static string CleanDeviceDesc(string desc)
        {
            if (string.IsNullOrEmpty(desc))
                return null;

            int idx = desc.LastIndexOf(';');
            string result = (idx >= 0 && idx + 1 < desc.Length)
                ? desc.Substring(idx + 1)
                : desc;

            return result.Trim();
        }

        /// <summary>
        /// Returns true if a device name is generic (not useful for identification).
        /// These are Windows default names that don't identify the specific hardware.
        /// </summary>
        private static bool IsGenericDeviceName(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            string lower = name.ToLowerInvariant();
            return lower.Contains("hid keyboard") ||
                   lower.Contains("hid-compliant") ||
                   lower.Contains("hid mouse") ||
                   lower.Contains("usb input device") ||
                   lower.Contains("usb composite device");
        }

        /// <summary>
        /// Attempts to read the HID product string from a device path.
        /// Returns null if the path is not a HID device or the query fails.
        /// </summary>
        private static string TryGetHidProductString(string devicePath)
        {
            if (string.IsNullOrEmpty(devicePath))
                return null;

            try
            {
                IntPtr handle = CreateFile(
                    devicePath,
                    0,  // No access rights needed for HidD_GetProductString
                    3,  // FILE_SHARE_READ | FILE_SHARE_WRITE
                    IntPtr.Zero,
                    3,  // OPEN_EXISTING
                    0,
                    IntPtr.Zero);

                if (handle == IntPtr.Zero || handle == INVALID_HANDLE_VALUE)
                    return null;

                try
                {
                    byte[] buffer = new byte[512];
                    if (HidD_GetProductString(handle, buffer, (uint)buffer.Length))
                    {
                        string name = Encoding.Unicode.GetString(buffer).TrimEnd('\0');
                        if (!string.IsNullOrWhiteSpace(name))
                            return name.Trim();
                    }
                    return null;
                }
                finally
                {
                    CloseHandle(handle);
                }
            }
            catch
            {
                return null;
            }
        }

        // ─────────────────────────────────────────────
        //  Public API — Per-Device State Access
        // ─────────────────────────────────────────────

        /// <summary>
        /// Sentinel handle for the "All Keyboards" aggregate device.
        /// </summary>
        public static readonly IntPtr AggregateKeyboardHandle = new IntPtr(-99);

        /// <summary>
        /// Sentinel handle for the "All Mice" aggregate device.
        /// Separate from keyboard to avoid cross-type lookup collisions.
        /// </summary>
        public static readonly IntPtr AggregateMouseHandle = new IntPtr(-98);

        /// <summary>
        /// Copies the keyboard state for a specific device into the destination array.
        /// Pass <see cref="AggregateKeyboardHandle"/> or <see cref="AggregateMouseHandle"/> to get the OR-merged state of all keyboards.
        /// </summary>
        public static void GetKeyboardState(IntPtr hDevice, bool[] dest, int count)
        {
            int n = Math.Min(count, 256);

            if (hDevice == AggregateKeyboardHandle)
            {
                // Merge all keyboard states: any key down on any device → true.
                foreach (var kvp in _keyboardStates)
                {
                    bool[] state = kvp.Value;
                    for (int i = 0; i < n; i++)
                    {
                        if (state[i])
                            dest[i] = true;
                    }
                }
                return;
            }

            if (_keyboardStates.TryGetValue(hDevice, out bool[] devState))
                Array.Copy(devState, dest, n);
            // If device hasn't sent any input yet, dest stays all-false (default).
        }

        /// <summary>
        /// Atomically consumes accumulated mouse deltas for a specific device.
        /// Pass <see cref="AggregateKeyboardHandle"/> or <see cref="AggregateMouseHandle"/> to consume summed deltas from all mice.
        /// </summary>
        public static void ConsumeMouseDelta(IntPtr hDevice, out int dx, out int dy)
        {
            if (hDevice == AggregateMouseHandle)
            {
                dx = (int)Interlocked.Exchange(ref _aggregateMouseState.DeltaX, 0);
                dy = (int)Interlocked.Exchange(ref _aggregateMouseState.DeltaY, 0);
                return;
            }

            if (_mouseStates.TryGetValue(hDevice, out MouseDeviceState state))
            {
                dx = (int)Interlocked.Exchange(ref state.DeltaX, 0);
                dy = (int)Interlocked.Exchange(ref state.DeltaY, 0);
            }
            else
            {
                dx = 0;
                dy = 0;
            }
        }

        /// <summary>
        /// Copies mouse button states for a specific device.
        /// Pass <see cref="AggregateKeyboardHandle"/> or <see cref="AggregateMouseHandle"/> to get OR-merged buttons from all mice.
        /// </summary>
        public static void GetMouseButtons(IntPtr hDevice, bool[] dest)
        {
            int n = Math.Min(dest.Length, 5);

            if (hDevice == AggregateMouseHandle)
            {
                Array.Copy(_aggregateMouseState.Buttons, dest, n);
                return;
            }

            if (_mouseStates.TryGetValue(hDevice, out MouseDeviceState state))
                Array.Copy(state.Buttons, dest, n);
        }

        // ─────────────────────────────────────────────
        //  Message Pump Thread
        // ─────────────────────────────────────────────

        private static void MessagePumpThread()
        {
            _wndProcDelegate = WndProc;
            IntPtr hInstance = GetModuleHandleW(null);
            string className = "PadForgeRawInput";
            IntPtr classNamePtr = Marshal.StringToHGlobalUni(className);

            try
            {
                var wc = new WNDCLASSEXW
                {
                    cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
                    hInstance = hInstance,
                    lpszClassName = classNamePtr
                };

                ushort atom = RegisterClassExW(ref wc);
                if (atom == 0)
                {
                    _running = false;
                    return;
                }

                _hwnd = CreateWindowExW(
                    0, (IntPtr)atom, null, 0,
                    0, 0, 0, 0,
                    HWND_MESSAGE, IntPtr.Zero, hInstance, IntPtr.Zero);

                if (_hwnd == IntPtr.Zero)
                {
                    _running = false;
                    return;
                }

                var devices = new RAWINPUTDEVICE[]
                {
                    new RAWINPUTDEVICE
                    {
                        usUsagePage = HID_USAGE_PAGE_GENERIC,
                        usUsage = HID_USAGE_GENERIC_KEYBOARD,
                        dwFlags = RIDEV_INPUTSINK,
                        hwndTarget = _hwnd
                    },
                    new RAWINPUTDEVICE
                    {
                        usUsagePage = HID_USAGE_PAGE_GENERIC,
                        usUsage = HID_USAGE_GENERIC_MOUSE,
                        dwFlags = RIDEV_INPUTSINK,
                        hwndTarget = _hwnd
                    }
                };

                if (!RegisterRawInputDevices(devices, 2, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
                {
                    DestroyWindow(_hwnd);
                    _hwnd = IntPtr.Zero;
                    _running = false;
                    return;
                }

                while (_running && GetMessageW(out MSG msg, IntPtr.Zero, 0, 0))
                {
                    TranslateMessage(ref msg);
                    DispatchMessageW(ref msg);
                }

                DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }
            finally
            {
                Marshal.FreeHGlobal(classNamePtr);
            }
        }

        // ─────────────────────────────────────────────
        //  Window Procedure
        // ─────────────────────────────────────────────

        private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_INPUT)
            {
                ProcessRawInput(lParam);
                return IntPtr.Zero;
            }
            return DefWindowProcW(hWnd, msg, wParam, lParam);
        }

        // ─────────────────────────────────────────────
        //  Raw Input Processing (per-device)
        // ─────────────────────────────────────────────

        [ThreadStatic]
        private static byte[] _rawInputBuffer;

        private static void ProcessRawInput(IntPtr lParam)
        {
            uint headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
            uint size = 0;

            GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref size, headerSize);
            if (size == 0) return;

            if (_rawInputBuffer == null || _rawInputBuffer.Length < (int)size)
                _rawInputBuffer = new byte[(int)size];

            GCHandle handle = GCHandle.Alloc(_rawInputBuffer, GCHandleType.Pinned);
            try
            {
                IntPtr pBuffer = handle.AddrOfPinnedObject();
                uint written = GetRawInputData(lParam, RID_INPUT, pBuffer, ref size, headerSize);
                if (written == unchecked((uint)-1)) return;

                var header = Marshal.PtrToStructure<RAWINPUTHEADER>(pBuffer);
                IntPtr dataPtr = IntPtr.Add(pBuffer, (int)headerSize);
                IntPtr hDevice = header.hDevice;

                if (header.dwType == RIM_TYPEKEYBOARD)
                {
                    var kb = Marshal.PtrToStructure<RAWKEYBOARD>(dataPtr);
                    int vk = kb.VKey;
                    if (vk >= 0 && vk < 256)
                    {
                        bool isDown = (kb.Flags & RI_KEY_BREAK) == 0;
                        bool[] state = _keyboardStates.GetOrAdd(hDevice, _ => new bool[256]);
                        state[vk] = isDown;
                    }
                }
                else if (header.dwType == RIM_TYPEMOUSE)
                {
                    var mouse = Marshal.PtrToStructure<RAWMOUSE>(dataPtr);
                    MouseDeviceState state = _mouseStates.GetOrAdd(hDevice, _ => new MouseDeviceState());

                    if (mouse.lLastX != 0)
                    {
                        Interlocked.Add(ref state.DeltaX, mouse.lLastX);
                        Interlocked.Add(ref _aggregateMouseState.DeltaX, mouse.lLastX);
                    }
                    if (mouse.lLastY != 0)
                    {
                        Interlocked.Add(ref state.DeltaY, mouse.lLastY);
                        Interlocked.Add(ref _aggregateMouseState.DeltaY, mouse.lLastY);
                    }

                    ushort flags = mouse.usButtonFlags;
                    if ((flags & RI_MOUSE_LEFT_BUTTON_DOWN) != 0) { state.Buttons[0] = true; _aggregateMouseState.Buttons[0] = true; }
                    if ((flags & RI_MOUSE_LEFT_BUTTON_UP) != 0) { state.Buttons[0] = false; _aggregateMouseState.Buttons[0] = false; }
                    if ((flags & RI_MOUSE_MIDDLE_BUTTON_DOWN) != 0) { state.Buttons[1] = true; _aggregateMouseState.Buttons[1] = true; }
                    if ((flags & RI_MOUSE_MIDDLE_BUTTON_UP) != 0) { state.Buttons[1] = false; _aggregateMouseState.Buttons[1] = false; }
                    if ((flags & RI_MOUSE_RIGHT_BUTTON_DOWN) != 0) { state.Buttons[2] = true; _aggregateMouseState.Buttons[2] = true; }
                    if ((flags & RI_MOUSE_RIGHT_BUTTON_UP) != 0) { state.Buttons[2] = false; _aggregateMouseState.Buttons[2] = false; }
                    if ((flags & RI_MOUSE_BUTTON_4_DOWN) != 0) { state.Buttons[3] = true; _aggregateMouseState.Buttons[3] = true; }
                    if ((flags & RI_MOUSE_BUTTON_4_UP) != 0) { state.Buttons[3] = false; _aggregateMouseState.Buttons[3] = false; }
                    if ((flags & RI_MOUSE_BUTTON_5_DOWN) != 0) { state.Buttons[4] = true; _aggregateMouseState.Buttons[4] = true; }
                    if ((flags & RI_MOUSE_BUTTON_5_UP) != 0) { state.Buttons[4] = false; _aggregateMouseState.Buttons[4] = false; }
                }
            }
            finally
            {
                handle.Free();
            }
        }
    }
}
