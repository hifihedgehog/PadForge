using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace PadForge.Engine
{
    /// <summary>
    /// Reads precision touchpad (PTP) input via Windows Raw Input API.
    /// Registers for HID Usage Page 0x0D (Digitizer), Usage 0x05 (Touch Pad)
    /// with RIDEV_INPUTSINK for background capture. Parses HID reports using
    /// HidP_* functions to extract per-finger contact data.
    ///
    /// Exposes normalized finger positions (0-1) and contact states for up to
    /// 2 fingers, matching the <see cref="Engine.TouchpadState"/> format used
    /// by the PlayStation touchpad pipeline.
    /// </summary>
    public sealed class PrecisionTouchpadReader : IDisposable
    {
        // ─────────────────────────────────────────────
        //  Constants
        // ─────────────────────────────────────────────

        private const int WM_INPUT = 0x00FF;
        private const int WM_INPUT_DEVICE_CHANGE = 0x00FE;
        private const int WM_QUIT = 0x0012;
        private const int GIDC_REMOVAL = 2;

        private const ushort HID_USAGE_PAGE_DIGITIZER = 0x0D;
        private const ushort HID_USAGE_DIGITIZER_TOUCH_PAD = 0x05;

        // HID Usage IDs within Digitizer page
        private const ushort HID_USAGE_CONTACT_COUNT = 0x54;
        private const ushort HID_USAGE_CONTACT_ID = 0x51;
        private const ushort HID_USAGE_DIGITIZER_TIP_SWITCH = 0x42;

        // HID Usage IDs within Generic Desktop page
        private const ushort HID_USAGE_PAGE_GENERIC = 0x01;
        private const ushort HID_USAGE_GENERIC_X = 0x30;
        private const ushort HID_USAGE_GENERIC_Y = 0x31;

        private const uint RIDEV_INPUTSINK = 0x00000100;
        private const uint RIDEV_DEVNOTIFY = 0x00002000;
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

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint GetRawInputDeviceInfoW(
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

        [DllImport("hid.dll")]
        private static extern uint HidP_GetUsages(
            int ReportType, ushort UsagePage, ushort LinkCollection,
            [Out] ushort[] UsageList, ref uint UsageLength, IntPtr PreparsedData,
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

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterClassW(IntPtr lpClassName, IntPtr hInstance);

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
        private WndProcDelegate _wndProcDelegate; // prevent GC collection
        private IntPtr _wndProcPtr;
        private ushort _classAtom; // uniquely-named window class, UnregisterClass-ed on teardown

        /// <summary>Cached preparsed data per device handle.</summary>
        private readonly Dictionary<IntPtr, IntPtr> _preparsedCache = new();
        private readonly Dictionary<IntPtr, HIDP_VALUE_CAPS[]> _valueCapsCache = new();
        private readonly Dictionary<IntPtr, (int logMinX, int logMaxX, int logMinY, int logMaxY)> _rangeCache = new();

        // Per-device output state (read by polling thread)
        private readonly object _stateLock = new();

        /// <summary>Per-device touchpad state keyed by RAWINPUT hDevice handle.</summary>
        private readonly Dictionary<IntPtr, PtpDeviceState> _deviceStates = new();

        /// <summary>Whether any precision touchpad device was detected.</summary>
        public bool IsAvailable { get; private set; }

        /// <summary>Maximum simultaneous contacts the PTP reader exposes
        /// per device. Windows Precision Touchpad HID spec maxes at 5
        /// fingers; the parser collects up to this many contacts per
        /// report and ignores any beyond. Sized as a constant rather
        /// than per-device because the HID descriptor's MaxContacts
        /// usage is read at registration time but we treat 5 as the
        /// canonical ceiling.</summary>
        public const int PtpMaxFingers = 5;

        /// <summary>Per-device touchpad state. Holds up to <see cref="PtpMaxFingers"/>
        /// simultaneous contact slots; flat arrays simplify the read
        /// path on the polling thread.</summary>
        internal class PtpDeviceState
        {
            public readonly float[] X = new float[PtpMaxFingers];
            public readonly float[] Y = new float[PtpMaxFingers];
            public readonly bool[] Down = new bool[PtpMaxFingers];

            // Persistent per-slot transition tracking. CustomInputState is
            // freshly allocated every polling tick (ReadDeviceState's tp =
            // state.Touchpads[0]), so the rising-edge test can't read
            // wasDown from tp.FingerDown — that always starts false and
            // every down-tick falsely registers as a new contact, which
            // breaks the gesture recognizer's per-(slot, contactId) path
            // continuity. These arrays survive across ticks and carry
            // the real previous-down state + the contact ID currently
            // assigned to whatever finger is on this slot.
            public readonly bool[] LastFrameDown = new bool[PtpMaxFingers];
            public readonly int[]  CurrentContactId;

            // HID contact ID assigned to each engine slot, or -1 when
            // the slot is free. Carries across frames so a finger
            // keeps the same slot even when lower-numbered slots
            // empty out and the device's buffer-arrival order shifts.
            // Without this, the engine's path data extends with the
            // wrong physical finger's coordinates after a multi-
            // finger lift and the resulting motion looks like a
            // swipe — taps stop firing.
            public readonly int[]  SlotToHidId;

            // Multi-report frame assembly. PTP devices commonly split a
            // single touch frame across multiple HID reports — the spec
            // (and most Windows-certified hardware) caps each report at
            // 2 contacts, with the contact-count usage on the first
            // report of a frame indicating the total expected. Without
            // accumulating across reports, only the last fragment's
            // contacts survive in ds.Down, so 3+ finger gestures never
            // observe all fingers down at once.
            public int FrameExpected;
            public int FrameSeen;
            /// <summary>Contacts in this frame that reported tip-switch = 0.
            /// They are deliberately NOT buffered as live fingers, but the
            /// frame's declared contactCount still counts them, so they must
            /// count toward completion or a lift frame can never satisfy
            /// FrameSeen >= FrameExpected.</summary>
            public int FrameLifted;
            public readonly float[] FrameBufX = new float[PtpMaxFingers];
            public readonly float[] FrameBufY = new float[PtpMaxFingers];
            public readonly int[]   FrameBufId = new int[PtpMaxFingers];

            public string Name = "Precision Touchpad";
            public string DevicePath = "";
            public ushort VendorId, ProductId;
            /// <summary>Timestamp of last WM_INPUT report for staleness detection.</summary>
            public long LastReportTicks;

            public PtpDeviceState()
            {
                // -1 = "no contact on this slot." Default int (0) would
                // look like a real contact ID to the recognizer.
                CurrentContactId = new int[PtpMaxFingers];
                SlotToHidId = new int[PtpMaxFingers];
                for (int i = 0; i < PtpMaxFingers; i++)
                {
                    CurrentContactId[i] = -1;
                    SlotToHidId[i] = -1;
                }
            }
        }

        /// <summary>Staleness threshold: clear contacts if no report in 100ms.</summary>
        private const long StaleThresholdTicks = 100 * TimeSpan.TicksPerMillisecond;

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
        /// Returns the currently known PTP device handles and their info.
        /// Called from Step 1 for device enumeration.
        /// </summary>
        public (IntPtr handle, string name, string path, ushort vid, ushort pid)[] GetDevices()
        {
            lock (_stateLock)
            {
                var result = new (IntPtr, string, string, ushort, ushort)[_deviceStates.Count];
                int i = 0;
                foreach (var kvp in _deviceStates)
                {
                    var ds = kvp.Value;
                    result[i++] = (kvp.Key, ds.Name, ds.DevicePath, ds.VendorId, ds.ProductId);
                }
                return result;
            }
        }

        /// <summary>
        /// Reads the current touchpad state for a specific device handle.
        /// Called from the polling thread (Step 2).
        /// </summary>
        public void ReadInto(IntPtr hDevice, Engine.CustomInputState state)
        {
            lock (_stateLock)
            {
                if (!_deviceStates.TryGetValue(hDevice, out var ds))
                    return;

                ReadDeviceState(ds, state);
            }
        }

        private void ReadDeviceState(PtpDeviceState ds, Engine.CustomInputState state)
        {
            // Clear contacts if no report received within staleness threshold.
            long now = DateTime.UtcNow.Ticks;
            if (ds.LastReportTicks > 0 && (now - ds.LastReportTicks) > StaleThresholdTicks)
            {
                for (int i = 0; i < PtpMaxFingers; i++)
                {
                    ds.Down[i] = false;
                    ds.SlotToHidId[i] = -1;
                }
                // A frame partially assembled then orphaned by silence
                // shouldn't carry into the next touch session.
                ds.FrameExpected = 0;
                ds.FrameSeen = 0;
                ds.FrameLifted = 0;
            }

            // PTP exposes a single touchpad surface with up to
            // PtpMaxFingers simultaneous contacts. Allocate the
            // TouchpadInputState[0] entry to the spec maximum so a
            // device that reports fewer fingers in a given report still
            // has the higher slots present (and quiet) for the gesture
            // engine. Contact IDs synthesize on rising/falling edges,
            // matching SdlDeviceWrapper's pattern; future HID-side
            // expansion can replace this with native HID contact IDs
            // from the report's contact-identifier usage.
            if (state.Touchpads == null || state.Touchpads.Length < 1
                || state.Touchpads[0] == null || state.Touchpads[0].MaxFingers < PtpMaxFingers)
            {
                state.Touchpads = new[] { new TouchpadInputState(PtpMaxFingers) };
            }
            var tp = state.Touchpads[0];

            for (int i = 0; i < PtpMaxFingers; i++)
            {
                tp.FingerX[i] = ds.X[i];
                tp.FingerY[i] = ds.Y[i];
                tp.FingerPressure[i] = ds.Down[i] ? 1f : 0f;

                // Transition test reads ds.LastFrameDown (persistent across
                // ticks), NOT tp.FingerDown (allocated fresh every tick and
                // thus always false at this point). Without this, every
                // continuous-touch tick looks like a rising edge and the
                // gesture engine sees N single-point paths instead of one
                // growing path — no gesture ever recognizes on PTP.
                bool wasDown = ds.LastFrameDown[i];
                bool isDown  = ds.Down[i];

                if (isDown && !wasDown)
                    ds.CurrentContactId[i] = _ptpContactIdNext++;
                else if (!isDown && wasDown)
                    ds.CurrentContactId[i] = -1;

                tp.FingerDown[i] = isDown;
                tp.FingerContactId[i] = ds.CurrentContactId[i];

                ds.LastFrameDown[i] = isDown;
            }
        }

        // Monotonic per-PTP-device contact ID source. Synthesizes contact
        // IDs from finger up/down transitions; future expansion will use
        // the native HID contact-identifier bytes from the PTP report
        // collection directly.
        private int _ptpContactIdNext = 1;

        /// <summary>
        /// Legacy: reads the first device's state (for backward compat).
        /// </summary>
        public void ReadInto(Engine.CustomInputState state)
        {
            lock (_stateLock)
            {
                foreach (var ds in _deviceStates.Values)
                {
                    ReadDeviceState(ds, state);
                    break;
                }
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
            _wndProcDelegate = WndProc;
            _wndProcPtr = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
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
            _classAtom = atom;

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
                    // DEVNOTIFY so the loop receives WM_INPUT_DEVICE_CHANGE and can
                    // free a removed touchpad's cached preparsed/HID buffers instead
                    // of holding every handle ever seen until Dispose.
                    dwFlags = RIDEV_INPUTSINK | RIDEV_DEVNOTIFY,
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

            // Unregister the uniquely-named window class so its atom doesn't leak
            // on every engine off/on toggle. Keep the unique name (a fixed name
            // shared across reader instances would bind a window to a stale
            // instance's WndProc); unregister it here per instance instead.
            if (_classAtom != 0)
            {
                UnregisterClassW((IntPtr)_classAtom, GetModuleHandleW(IntPtr.Zero));
                _classAtom = 0;
            }
        }

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_INPUT)
            {
                ProcessRawInput(lParam);
                return IntPtr.Zero;
            }
            if (msg == WM_INPUT_DEVICE_CHANGE)
            {
                // A touchpad was removed: free its cached preparsed HGLOBAL and
                // drop the value-caps / range entries so they don't accumulate
                // across device churn. Runs on the message-loop thread, the same
                // thread that populates these caches in ProcessRawInput, so no
                // extra synchronization is required.
                if ((int)wParam == GIDC_REMOVAL)
                {
                    IntPtr removed = lParam;
                    if (_preparsedCache.TryGetValue(removed, out var pp))
                    {
                        Marshal.FreeHGlobal(pp);
                        _preparsedCache.Remove(removed);
                    }
                    _valueCapsCache.Remove(removed);
                    _rangeCache.Remove(removed);
                    // And the device state itself. Dropping only the three
                    // caches left the _deviceStates entry behind, and that
                    // dictionary is what GetDevices enumerates, so an
                    // unplugged precision touchpad stayed enumerated and
                    // reported Online for the rest of the session. Step 1's
                    // disconnect branch keys on the device vanishing from that
                    // enumeration, so it was unreachable for touchpads.
                    // _stateLock, the same monitor every other _deviceStates
                    // access takes. The three caches above are message-loop
                    // only, but this dictionary is read from other threads
                    // (GetDevices, the per-device readers).
                    lock (_stateLock)
                        _deviceStates.Remove(removed);
                }
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
                    ParseTouchpadReport(header.hDevice, preparsed, report, rawHid.dwSizeHid, valueCaps, ranges);
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

        /// <summary>Reads the tip-switch (button 0x42 on the digitizer
        /// page) for one per-finger link collection. Returns true when
        /// the contact is actually touching the surface, false when
        /// the contact is in the report only to announce a lift, and
        /// true again as a defensive fallback when the HID call fails
        /// (preserves legacy "treat report-present as touching"
        /// behavior on devices that don't expose tip-switch). The
        /// per-call ushort[8] stack scratch is plenty: a digitizer
        /// link collection's button page typically holds tip-switch +
        /// in-range + confidence at most.</summary>
        // Reused scratch: ReadTipSwitch runs per contact per report on
        // the single raw-input message thread (~125-250 Hz x contacts
        // while a finger is down), so the per-call array was steady churn.
        private static readonly ushort[] s_tipUsageScratch = new ushort[8];

        private static bool ReadTipSwitch(IntPtr preparsed, IntPtr report,
            uint reportLength, ushort linkCollection)
        {
            var usageList = s_tipUsageScratch;
            uint length = (uint)usageList.Length;
            uint hr = HidP_GetUsages(HidP_Input, HID_USAGE_PAGE_DIGITIZER,
                linkCollection, usageList, ref length, preparsed, report, reportLength);
            if (hr != HIDP_STATUS_SUCCESS) return true;
            for (uint i = 0; i < length; i++)
                if (usageList[i] == HID_USAGE_DIGITIZER_TIP_SWITCH) return true;
            return false;
        }

        // Reused per-report contact scratch (single raw-input thread;
        // fully consumed into FrameBuf* before return).
        private readonly List<(float x, float y, int id)> _fingersScratch = new();

        private void ParseTouchpadReport(IntPtr hDevice, IntPtr preparsed, IntPtr report, uint reportLength,
            HIDP_VALUE_CAPS[] valueCaps, (int logMinX, int logMaxX, int logMinY, int logMaxY) ranges)
        {
            // Read contact count for THIS report. PTP spec: the first
            // report of a touch frame carries the total expected contact
            // count in this usage; subsequent fragments in the same
            // frame carry zero. The reader uses this to assemble a
            // multi-report frame before committing — see frame-assembly
            // block below.
            HidP_GetUsageValue(HidP_Input, HID_USAGE_PAGE_DIGITIZER, 0,
                HID_USAGE_CONTACT_COUNT, out uint contactCount, preparsed, report, reportLength);

            // Parse this report's contacts. The reader iterates the
            // contact-ID-bearing link collections — each represents one
            // finger slot in the report descriptor. Most certified PTP
            // hardware caps a single report at 2 contacts; we read up
            // to PtpMaxFingers defensively in case a parallel-mode
            // device packs all fingers into one report.
            var fingers = _fingersScratch;
            fingers.Clear();
            // Contacts in THIS report that reported tip-switch = 0. They are
            // not buffered as live fingers, but the frame's declared
            // contactCount counts them, so they must count toward completion.
            int liftedThisReport = 0;

            foreach (var vc in valueCaps)
            {
                ushort usage = vc.IsRange ? vc.UsageMin : vc.UsageMin;

                if (vc.UsagePage == HID_USAGE_PAGE_DIGITIZER && usage == HID_USAGE_CONTACT_ID)
                {
                    ushort linkCollection = vc.LinkCollection;

                    if (HidP_GetUsageValue(HidP_Input, HID_USAGE_PAGE_DIGITIZER, linkCollection,
                            HID_USAGE_CONTACT_ID, out uint contactId, preparsed, report, reportLength)
                        != HIDP_STATUS_SUCCESS)
                        continue;

                    // Tip-switch is authoritative for "is this finger
                    // touching right now." Per the PTP spec, when a
                    // finger lifts, the device sends one final report
                    // for that contact with tip-switch = 0 and the
                    // last X/Y position. Without this check, that
                    // lift-frame entry inflates the apparent contact
                    // count and the gesture engine sees a finger that
                    // physically isn't there anymore. On 5-finger
                    // taps every lift report contributes one phantom
                    // contact — fingerCount overshoots, no tap fires.
                    if (!ReadTipSwitch(preparsed, report, reportLength, linkCollection))
                    {
                        // Counted, not buffered. The frame's contactCount
                        // includes this lifted contact, so skipping it
                        // silently left FrameSeen permanently short of
                        // FrameExpected and the frame never completed: the
                        // release was deferred to the 100 ms staleness timer,
                        // or swallowed outright by the next tap.
                        liftedThisReport++;
                        continue;
                    }

                    if (HidP_GetUsageValue(HidP_Input, HID_USAGE_PAGE_GENERIC, linkCollection,
                            HID_USAGE_GENERIC_X, out uint rawX, preparsed, report, reportLength)
                        != HIDP_STATUS_SUCCESS)
                        continue;

                    if (HidP_GetUsageValue(HidP_Input, HID_USAGE_PAGE_GENERIC, linkCollection,
                            HID_USAGE_GENERIC_Y, out uint rawY, preparsed, report, reportLength)
                        != HIDP_STATUS_SUCCESS)
                        continue;

                    float x = (ranges.logMaxX > ranges.logMinX)
                        ? (float)(rawX - ranges.logMinX) / (ranges.logMaxX - ranges.logMinX)
                        : 0f;
                    float y = (ranges.logMaxY > ranges.logMinY)
                        ? (float)(rawY - ranges.logMinY) / (ranges.logMaxY - ranges.logMinY)
                        : 0f;

                    x = Math.Clamp(x, 0f, 1f);
                    y = Math.Clamp(y, 0f, 1f);

                    fingers.Add((x, y, (int)contactId));
                    if (fingers.Count >= PtpMaxFingers) break;
                }
            }

            // Update per-device state.
            lock (_stateLock)
            {
                if (!_deviceStates.TryGetValue(hDevice, out var ds))
                {
                    ds = new PtpDeviceState();
                    PopulateDeviceInfo(hDevice, ds);
                    _deviceStates[hDevice] = ds;
                }

                ds.LastReportTicks = DateTime.UtcNow.Ticks;

                // Frame-assembly: PTP frames span multiple reports on
                // most hardware (≤2 contacts per report; total carried
                // on the first report's contact-count). Without this,
                // the reader sees at most the LAST report's contacts
                // and 3+ finger gestures never observe a complete frame.
                //
                // Rules:
                //   - contactCount > 0 marks the start of a new frame.
                //     Reset the buffer (any partial-prior is truncated)
                //     and set the expected total.
                //   - contactCount == 0 marks a continuation of the
                //     in-progress frame.
                //   - Commit when the buffer reaches the expected count.
                //   - If a device never sets contact-count (out-of-spec),
                //     FrameExpected stays 0 and every report commits as
                //     its own frame — the legacy behavior, preserved as
                //     fallback.
                if (contactCount > 0)
                {
                    if (ds.FrameSeen > 0 && (int)contactCount != ds.FrameExpected)
                    {
                        // Partial prior frame mismatches new total →
                        // truncated. Discard.
                        ds.FrameSeen = 0;
                        ds.FrameLifted = 0;
                    }
                    ds.FrameExpected = System.Math.Min((int)contactCount, PtpMaxFingers);
                }

                // Append this report's contacts, bounded by what the
                // frame still expects. The HID descriptor commonly
                // exposes more contact link-collections than the frame
                // actually carries — empty slots return stale or zero
                // X/Y but still parse as "contacts." Without the
                // FrameExpected cap, a 2-finger report on a 5-slot
                // descriptor commits as a 5-finger frame and the
                // gesture engine misclassifies the tap.
                int appendLimit = ds.FrameExpected > 0
                    ? System.Math.Min(fingers.Count, ds.FrameExpected - ds.FrameSeen)
                    : fingers.Count;
                for (int i = 0; i < appendLimit && ds.FrameSeen < PtpMaxFingers; i++)
                {
                    ds.FrameBufX[ds.FrameSeen] = fingers[i].x;
                    ds.FrameBufY[ds.FrameSeen] = fingers[i].y;
                    ds.FrameBufId[ds.FrameSeen] = fingers[i].id;
                    ds.FrameSeen++;
                }

                ds.FrameLifted += liftedThisReport;

                bool frameComplete =
                    (ds.FrameExpected > 0 && ds.FrameSeen + ds.FrameLifted >= ds.FrameExpected)
                    || (ds.FrameExpected == 0);

                if (!frameComplete) return;

                // Commit the assembled frame with slot-stable assignment.
                // Each buffered contact's HID contact ID looks up its
                // existing engine-slot index first (pass 1); IDs not
                // already mapped claim the lowest free slot (pass 2).
                // Slots whose HID ID is no longer in the frame are
                // released. This keeps a finger's engine slot stable
                // across frames even when lower slots empty out.
                System.Span<int>  assign  = stackalloc int [PtpMaxFingers];
                System.Span<bool> claimed = stackalloc bool[PtpMaxFingers];
                int n = System.Math.Min(ds.FrameSeen, PtpMaxFingers);
                for (int i = 0; i < n; i++) assign[i] = -1;

                for (int i = 0; i < n; i++)
                {
                    int id = ds.FrameBufId[i];
                    for (int s = 0; s < PtpMaxFingers; s++)
                    {
                        if (ds.SlotToHidId[s] == id)
                        {
                            assign[i] = s;
                            claimed[s] = true;
                            break;
                        }
                    }
                }

                for (int i = 0; i < n; i++)
                {
                    if (assign[i] >= 0) continue;
                    for (int s = 0; s < PtpMaxFingers; s++)
                    {
                        if (claimed[s] || ds.SlotToHidId[s] >= 0) continue;
                        assign[i] = s;
                        claimed[s] = true;
                        ds.SlotToHidId[s] = ds.FrameBufId[i];
                        break;
                    }
                }

                // Release slots whose HID ID dropped out of this frame.
                // The synth-cid pass in ReadDeviceState picks up the
                // wasDown→!isDown transition from the cleared Down[]
                // below and assigns -1 to CurrentContactId for these
                // slots, terminating their paths cleanly.
                for (int s = 0; s < PtpMaxFingers; s++)
                {
                    if (claimed[s]) continue;
                    ds.SlotToHidId[s] = -1;
                }

                for (int i = 0; i < PtpMaxFingers; i++) ds.Down[i] = false;
                for (int i = 0; i < n; i++)
                {
                    int s = assign[i];
                    if (s < 0) continue;
                    ds.X[s] = ds.FrameBufX[i];
                    ds.Y[s] = ds.FrameBufY[i];
                    ds.Down[s] = true;
                }
                ds.FrameExpected = 0;
                ds.FrameSeen = 0;
                // The lift tally is part of the frame counter, so it resets with
                // it. Leaving it set here let a lift from the frame just
                // committed count toward the NEXT frame's completion test
                // (FrameSeen + FrameLifted >= FrameExpected), which fired that
                // frame early: on a pad that carries two contacts per report, a
                // three-finger frame completed on its first report with two
                // fingers and the third committed as a frame of its own.
                ds.FrameLifted = 0;
            }
        }

        /// <summary>Populates device name/VID/PID from Raw Input device info.</summary>
        private void PopulateDeviceInfo(IntPtr hDevice, PtpDeviceState ds)
        {
            try
            {
                // Get device path via RIDI_DEVICENAME (Unicode W variant).
                uint nameSize = 0;
                GetRawInputDeviceInfoW(hDevice, 0x20000007, IntPtr.Zero, ref nameSize);
                if (nameSize > 0)
                {
                    IntPtr nameBuf = Marshal.AllocHGlobal((int)nameSize * 2);
                    try
                    {
                        if (GetRawInputDeviceInfoW(hDevice, 0x20000007, nameBuf, ref nameSize) > 0)
                            ds.DevicePath = Marshal.PtrToStringUni(nameBuf) ?? "";
                    }
                    finally { Marshal.FreeHGlobal(nameBuf); }
                }

                // Use RawInputListener's proven 3-method VID/PID extraction
                // (HidD_GetAttributes → RIDI_DEVICEINFO → path parsing).
                RawInputListener.GetDeviceVidPid(hDevice, ds.DevicePath,
                    out ds.VendorId, out ds.ProductId);

                // Use RawInputListener's proven 5-method friendly name extraction
                // (HidD_GetProductString → USB parent registry → device registry →
                // VID:PID label → generic fallback). Type 2 = RIM_TYPEHID.
                if (!string.IsNullOrEmpty(ds.DevicePath))
                    ds.Name = RawInputListener.ExtractFriendlyName(ds.DevicePath, 2);
            }
            catch { }
        }

    }
}
