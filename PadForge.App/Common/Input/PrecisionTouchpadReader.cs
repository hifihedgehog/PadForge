using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Reads precision touchpad (PTP) input via Windows Raw Input API.
    /// Registers for HID Usage Page 0x0D (Digitizer), Usage 0x05 (Touch Pad)
    /// with RIDEV_INPUTSINK for background capture. Parses HID reports using
    /// HidP_* functions to extract per-finger contact data.
    ///
    /// Exposes normalized finger positions (0-1) and contact states for up to
    /// 2 fingers, matching the <see cref="Engine.TouchpadState"/> format used
    /// by the DS4 touchpad pipeline.
    /// </summary>
    internal sealed class PrecisionTouchpadReader : IDisposable
    {
        // ─────────────────────────────────────────────
        //  Constants
        // ─────────────────────────────────────────────

        private const int WM_INPUT = 0x00FF;
        private const int WM_QUIT = 0x0012;

        private const ushort HID_USAGE_PAGE_DIGITIZER = 0x0D;
        private const ushort HID_USAGE_DIGITIZER_TOUCH_PAD = 0x05;

        // HID Usage IDs within Digitizer page
        private const ushort HID_USAGE_CONTACT_COUNT = 0x54;
        private const ushort HID_USAGE_CONTACT_ID = 0x51;

        // HID Usage IDs within Generic Desktop page
        private const ushort HID_USAGE_PAGE_GENERIC = 0x01;
        private const ushort HID_USAGE_GENERIC_X = 0x30;
        private const ushort HID_USAGE_GENERIC_Y = 0x31;

        private const uint RIDEV_INPUTSINK = 0x00000100;
        private const uint RID_INPUT = 0x10000003;
        private const uint RIM_TYPEHID = 2;

        private const uint RIDI_PREPARSEDDATA = 0x20000005;

        // HIDP_STATUS codes
        private const uint HIDP_STATUS_SUCCESS = 0x00110000;

        // HidP Report Type
        private const int HidP_Input = 0;

        private static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);

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
        private struct RAWHID
        {
            public uint dwSizeHid;
            public uint dwCount;
            // bRawData follows (variable length)
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
        private struct HIDP_CAPS
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

        [StructLayout(LayoutKind.Sequential)]
        private struct HIDP_VALUE_CAPS
        {
            public ushort UsagePage;
            public byte ReportID;
            [MarshalAs(UnmanagedType.U1)]
            public bool IsAlias;
            public ushort BitField;
            public ushort LinkCollection;
            public ushort LinkUsage;
            public ushort LinkUsagePage;
            [MarshalAs(UnmanagedType.U1)]
            public bool IsRange;
            [MarshalAs(UnmanagedType.U1)]
            public bool IsStringRange;
            [MarshalAs(UnmanagedType.U1)]
            public bool IsDesignatorRange;
            [MarshalAs(UnmanagedType.U1)]
            public bool IsAbsolute;
            [MarshalAs(UnmanagedType.U1)]
            public bool HasNull;
            public byte Reserved;
            public ushort BitSize;
            public ushort ReportCount;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 5)]
            public ushort[] Reserved2;
            public uint UnitsExp;
            public uint Units;
            public int LogicalMin;
            public int LogicalMax;
            public int PhysicalMin;
            public int PhysicalMax;
            // Union: Range vs NotRange
            public ushort UsageMin;     // NotRange.Usage when !IsRange
            public ushort UsageMax;
            public ushort StringMin;
            public ushort StringMax;
            public ushort DesignatorMin;
            public ushort DesignatorMax;
            public ushort DataIndexMin;
            public ushort DataIndexMax;
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
        private static extern uint GetRawInputDeviceInfo(
            IntPtr hDevice, uint uiCommand, IntPtr pData, ref uint pcbSize);

        [DllImport("hid.dll")]
        private static extern uint HidP_GetCaps(IntPtr PreparsedData, ref HIDP_CAPS Capabilities);

        [DllImport("hid.dll")]
        private static extern uint HidP_GetValueCaps(
            int ReportType, [Out] HIDP_VALUE_CAPS[] ValueCaps,
            ref ushort ValueCapsLength, IntPtr PreparsedData);

        [DllImport("hid.dll")]
        private static extern uint HidP_GetUsageValue(
            int ReportType, ushort UsagePage, ushort LinkCollection,
            ushort Usage, out uint UsageValue, IntPtr PreparsedData,
            IntPtr Report, uint ReportLength);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowExW(
            uint dwExStyle, IntPtr lpClassName, IntPtr lpWindowName, uint dwStyle,
            int x, int y, int w, int h, IntPtr hWndParent, IntPtr hMenu,
            IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMessageW(ref MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessageW(ref MSG lpMsg);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandleW(IntPtr lpModuleName);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        // ─────────────────────────────────────────────
        //  State
        // ─────────────────────────────────────────────

        private Thread _thread;
        private uint _threadId;
        private volatile bool _running;
        private IntPtr _hwnd;
        private IntPtr _wndProcPtr;

        /// <summary>Cached preparsed data per device handle.</summary>
        private readonly Dictionary<IntPtr, IntPtr> _preparsedCache = new();
        private readonly Dictionary<IntPtr, HIDP_VALUE_CAPS[]> _valueCapsCache = new();
        private readonly Dictionary<IntPtr, (int logMinX, int logMaxX, int logMinY, int logMaxY)> _rangeCache = new();

        // Output state (read by polling thread)
        private readonly object _stateLock = new();
        private float _x0, _y0, _x1, _y1;
        private bool _down0, _down1;
        private int _contactCount;

        /// <summary>Whether any precision touchpad device was detected.</summary>
        public bool IsAvailable { get; private set; }

        // ─────────────────────────────────────────────
        //  Public API
        // ─────────────────────────────────────────────

        /// <summary>
        /// Starts the background thread that registers for PTP raw input
        /// and processes WM_INPUT messages.
        /// </summary>
        public void Start()
        {
            if (_running) return;
            _running = true;
            _thread = new Thread(MessageLoop) { IsBackground = true, Name = "PTP-RawInput" };
            _thread.Start();
        }

        /// <summary>Stops the message loop and cleans up.</summary>
        public void Stop()
        {
            if (!_running) return;
            _running = false;
            if (_threadId != 0)
                PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _thread?.Join(2000);
        }

        /// <summary>
        /// Reads the current touchpad state into the provided CustomInputState.
        /// Called from the polling thread (Step 2).
        /// </summary>
        public void ReadInto(Engine.CustomInputState state)
        {
            lock (_stateLock)
            {
                state.TouchpadFingers[0] = _x0;
                state.TouchpadFingers[1] = _y0;
                state.TouchpadFingers[2] = _down0 ? 1f : 0f; // pressure
                state.TouchpadDown[0] = _down0;

                state.TouchpadFingers[3] = _x1;
                state.TouchpadFingers[4] = _y1;
                state.TouchpadFingers[5] = _down1 ? 1f : 0f;
                state.TouchpadDown[1] = _down1;
            }
        }

        public void Dispose()
        {
            Stop();
            foreach (var ptr in _preparsedCache.Values)
                Marshal.FreeHGlobal(ptr);
            _preparsedCache.Clear();
            _valueCapsCache.Clear();
            _rangeCache.Clear();
        }

        // ─────────────────────────────────────────────
        //  Message Loop (background thread)
        // ─────────────────────────────────────────────

        private void MessageLoop()
        {
            _threadId = GetCurrentThreadId();

            // Register window class
            _wndProcPtr = Marshal.GetFunctionPointerForDelegate<WndProcDelegate>(WndProc);
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = _wndProcPtr,
                hInstance = GetModuleHandleW(IntPtr.Zero),
                lpszClassName = Marshal.StringToHGlobalUni("PadForge_PTP_" + Environment.TickCount64)
            };

            ushort atom = RegisterClassExW(ref wc);
            if (atom == 0)
            {
                Marshal.FreeHGlobal(wc.lpszClassName);
                _running = false;
                return;
            }

            _hwnd = CreateWindowExW(0, (IntPtr)atom, IntPtr.Zero, 0,
                0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, wc.hInstance, IntPtr.Zero);

            Marshal.FreeHGlobal(wc.lpszClassName);

            if (_hwnd == IntPtr.Zero)
            {
                _running = false;
                return;
            }

            // Register for Precision Touchpad
            var devices = new RAWINPUTDEVICE[]
            {
                new RAWINPUTDEVICE
                {
                    usUsagePage = HID_USAGE_PAGE_DIGITIZER,
                    usUsage = HID_USAGE_DIGITIZER_TOUCH_PAD,
                    dwFlags = RIDEV_INPUTSINK,
                    hwndTarget = _hwnd
                }
            };

            if (!RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
            {
                DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
                _running = false;
                return;
            }

            IsAvailable = true;

            // Message pump
            var msg = new MSG();
            while (_running && GetMessageW(ref msg, IntPtr.Zero, 0, 0))
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }

            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_INPUT)
            {
                ProcessRawInput(lParam);
                return IntPtr.Zero;
            }
            return DefWindowProcW(hWnd, msg, wParam, lParam);
        }

        // ─────────────────────────────────────────────
        //  HID Report Processing
        // ─────────────────────────────────────────────

        private void ProcessRawInput(IntPtr lParam)
        {
            uint headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
            uint size = 0;
            GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref size, headerSize);
            if (size == 0) return;

            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (GetRawInputData(lParam, RID_INPUT, buffer, ref size, headerSize) == unchecked((uint)-1))
                    return;

                var header = Marshal.PtrToStructure<RAWINPUTHEADER>(buffer);
                if (header.dwType != RIM_TYPEHID)
                    return;

                // Read RAWHID header (after RAWINPUTHEADER)
                IntPtr rawHidPtr = buffer + (int)headerSize;
                var rawHid = Marshal.PtrToStructure<RAWHID>(rawHidPtr);
                if (rawHid.dwSizeHid == 0 || rawHid.dwCount == 0)
                    return;

                // Get preparsed data for this device
                IntPtr preparsed = GetOrCachePreparsedData(header.hDevice);
                if (preparsed == IntPtr.Zero) return;

                // Get value caps
                var valueCaps = GetOrCacheValueCaps(header.hDevice, preparsed);
                if (valueCaps == null || valueCaps.Length == 0) return;

                // Get coordinate ranges
                var ranges = GetOrCacheRanges(header.hDevice, valueCaps);

                // Parse each HID report in the input
                IntPtr reportData = rawHidPtr + Marshal.SizeOf<RAWHID>();
                for (uint r = 0; r < rawHid.dwCount; r++)
                {
                    IntPtr report = reportData + (int)(r * rawHid.dwSizeHid);
                    ParseTouchpadReport(preparsed, report, rawHid.dwSizeHid, valueCaps, ranges);
                }
            }
            catch { }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private IntPtr GetOrCachePreparsedData(IntPtr hDevice)
        {
            if (_preparsedCache.TryGetValue(hDevice, out var cached))
                return cached;

            uint ppSize = 0;
            GetRawInputDeviceInfo(hDevice, RIDI_PREPARSEDDATA, IntPtr.Zero, ref ppSize);
            if (ppSize == 0) return IntPtr.Zero;

            IntPtr ppData = Marshal.AllocHGlobal((int)ppSize);
            if (GetRawInputDeviceInfo(hDevice, RIDI_PREPARSEDDATA, ppData, ref ppSize) == unchecked((uint)-1))
            {
                Marshal.FreeHGlobal(ppData);
                return IntPtr.Zero;
            }

            _preparsedCache[hDevice] = ppData;
            return ppData;
        }

        private HIDP_VALUE_CAPS[] GetOrCacheValueCaps(IntPtr hDevice, IntPtr preparsed)
        {
            if (_valueCapsCache.TryGetValue(hDevice, out var cached))
                return cached;

            var caps = new HIDP_CAPS();
            if (HidP_GetCaps(preparsed, ref caps) != HIDP_STATUS_SUCCESS)
                return null;

            ushort numValueCaps = caps.NumberInputValueCaps;
            if (numValueCaps == 0) return null;

            var valueCaps = new HIDP_VALUE_CAPS[numValueCaps];
            if (HidP_GetValueCaps(HidP_Input, valueCaps, ref numValueCaps, preparsed) != HIDP_STATUS_SUCCESS)
                return null;

            _valueCapsCache[hDevice] = valueCaps;
            return valueCaps;
        }

        private (int, int, int, int) GetOrCacheRanges(IntPtr hDevice, HIDP_VALUE_CAPS[] valueCaps)
        {
            if (_rangeCache.TryGetValue(hDevice, out var cached))
                return cached;

            int logMinX = 0, logMaxX = 1, logMinY = 0, logMaxY = 1;

            foreach (var vc in valueCaps)
            {
                ushort usage = vc.IsRange ? vc.UsageMin : vc.UsageMin; // NotRange.Usage stored in UsageMin

                if (vc.UsagePage == HID_USAGE_PAGE_GENERIC && usage == HID_USAGE_GENERIC_X)
                {
                    logMinX = vc.LogicalMin;
                    logMaxX = vc.LogicalMax;
                }
                else if (vc.UsagePage == HID_USAGE_PAGE_GENERIC && usage == HID_USAGE_GENERIC_Y)
                {
                    logMinY = vc.LogicalMin;
                    logMaxY = vc.LogicalMax;
                }
            }

            var ranges = (logMinX, logMaxX, logMinY, logMaxY);
            _rangeCache[hDevice] = ranges;
            return ranges;
        }

        private void ParseTouchpadReport(IntPtr preparsed, IntPtr report, uint reportLength,
            HIDP_VALUE_CAPS[] valueCaps, (int logMinX, int logMaxX, int logMinY, int logMaxY) ranges)
        {
            // Read contact count
            HidP_GetUsageValue(HidP_Input, HID_USAGE_PAGE_DIGITIZER, 0,
                HID_USAGE_CONTACT_COUNT, out uint contactCount, preparsed, report, reportLength);

            // Parse up to 2 fingers by iterating link collections.
            // Each finger is in its own link collection with Contact ID, X, Y.
            var fingers = new List<(float x, float y, int id)>();

            foreach (var vc in valueCaps)
            {
                ushort usage = vc.IsRange ? vc.UsageMin : vc.UsageMin;

                // Find Contact ID entries — each one indicates a finger's link collection
                if (vc.UsagePage == HID_USAGE_PAGE_DIGITIZER && usage == HID_USAGE_CONTACT_ID)
                {
                    ushort linkCollection = vc.LinkCollection;

                    // Read Contact ID
                    if (HidP_GetUsageValue(HidP_Input, HID_USAGE_PAGE_DIGITIZER, linkCollection,
                            HID_USAGE_CONTACT_ID, out uint contactId, preparsed, report, reportLength)
                        != HIDP_STATUS_SUCCESS)
                        continue;

                    // Read X
                    if (HidP_GetUsageValue(HidP_Input, HID_USAGE_PAGE_GENERIC, linkCollection,
                            HID_USAGE_GENERIC_X, out uint rawX, preparsed, report, reportLength)
                        != HIDP_STATUS_SUCCESS)
                        continue;

                    // Read Y
                    if (HidP_GetUsageValue(HidP_Input, HID_USAGE_PAGE_GENERIC, linkCollection,
                            HID_USAGE_GENERIC_Y, out uint rawY, preparsed, report, reportLength)
                        != HIDP_STATUS_SUCCESS)
                        continue;

                    // Normalize to 0-1
                    float x = (ranges.logMaxX > ranges.logMinX)
                        ? (float)(rawX - ranges.logMinX) / (ranges.logMaxX - ranges.logMinX)
                        : 0f;
                    float y = (ranges.logMaxY > ranges.logMinY)
                        ? (float)(rawY - ranges.logMinY) / (ranges.logMaxY - ranges.logMinY)
                        : 0f;

                    x = Math.Clamp(x, 0f, 1f);
                    y = Math.Clamp(y, 0f, 1f);

                    fingers.Add((x, y, (int)contactId));
                    if (fingers.Count >= 2) break;
                }
            }

            // Update state
            lock (_stateLock)
            {
                _contactCount = (int)contactCount;

                if (contactCount >= 1 && fingers.Count >= 1)
                {
                    _x0 = fingers[0].x;
                    _y0 = fingers[0].y;
                    _down0 = true;
                }
                else
                {
                    _down0 = false;
                }

                if (contactCount >= 2 && fingers.Count >= 2)
                {
                    _x1 = fingers[1].x;
                    _y1 = fingers[1].y;
                    _down1 = true;
                }
                else
                {
                    _down1 = false;
                }
            }
        }
    }
}
