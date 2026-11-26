using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace PadForge.Tools.OutputFamilyProbe
{
    /// <summary>One Raw Input event: the device it came from, by the
    /// instance ID its name gives, and what it carried.</summary>
    internal readonly record struct RawEvent(string InstanceId, bool Keyboard, ushort VKey, ushort MakeCode, bool Break, int X, int Y);

    /// <summary>Raw Input for keyboards and mice on a message-only window of
    /// the probe's own, on a thread of its own. RIDEV_INPUTSINK delivers the
    /// input while the window has no focus, which a message-only window never
    /// has. The legacy messages are left on, so the rest of the desktop sees
    /// input as before.</summary>
    internal sealed unsafe class RawInputReader : IDisposable
    {
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new(false);
        private readonly ConcurrentQueue<RawEvent> _events = new();
        private IntPtr _hwnd;
        private string _error;
        private Native.WndProc _proc;
        private const string ClassName = "PadForgeOutputFamilyProbeRawInput";

        public RawInputReader()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "Raw Input" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            _ready.Wait(5000);
        }

        public string Error => _error;

        public bool TryTake(out RawEvent e) => _events.TryDequeue(out e);

        public void Clear() { while (_events.TryDequeue(out _)) { } }

        private void Run()
        {
            try
            {
                _proc = Proc;
                IntPtr instance = Native.GetModuleHandleW(null);
                var cls = new Native.WNDCLASSEXW
                {
                    cbSize = Marshal.SizeOf<Native.WNDCLASSEXW>(),
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
                    hInstance = instance,
                    lpszClassName = ClassName,
                };
                Native.RegisterClassExW(ref cls);
                _hwnd = Native.CreateWindowExW(0, ClassName, ClassName, 0, 0, 0, 0, 0, Native.HWND_MESSAGE, IntPtr.Zero, instance, IntPtr.Zero);
                if (_hwnd == IntPtr.Zero)
                {
                    _error = $"CreateWindowEx failed, error {Marshal.GetLastWin32Error()}";
                    return;
                }
                var devices = new[]
                {
                    new Native.RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x06, dwFlags = Native.RIDEV_INPUTSINK, hwndTarget = _hwnd },
                    new Native.RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x02, dwFlags = Native.RIDEV_INPUTSINK, hwndTarget = _hwnd },
                };
                if (!Native.RegisterRawInputDevices(devices, devices.Length, Marshal.SizeOf<Native.RAWINPUTDEVICE>()))
                {
                    _error = $"RegisterRawInputDevices failed, error {Marshal.GetLastWin32Error()}";
                    Native.DestroyWindow(_hwnd);
                    _hwnd = IntPtr.Zero;
                    return;
                }
            }
            finally { _ready.Set(); }

            while (Native.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                Native.TranslateMessage(ref msg);
                Native.DispatchMessageW(ref msg);
            }
        }

        private readonly Dictionary<IntPtr, string> _names = new();

        private string InstanceOf(IntPtr device)
        {
            if (_names.TryGetValue(device, out var known)) return known;
            int size = 0;
            Native.GetRawInputDeviceInfoW(device, Native.RIDI_DEVICENAME, null, ref size);
            string id = string.Empty;
            if (size > 0)
            {
                var buf = new char[size + 1];
                if (Native.GetRawInputDeviceInfoW(device, Native.RIDI_DEVICENAME, buf, ref size) > 0)
                    id = InstanceIdOfPath(new string(buf).TrimEnd('\0'));
            }
            _names[device] = id;
            return id;
        }

        /// <summary>A device interface path's instance ID: the part between
        /// the "\\?\" prefix and the trailing "#{class guid}", with '#' turned
        /// back into '\', as Windows builds the path from the instance
        /// ID.</summary>
        internal static string InstanceIdOfPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            string p = path;
            if (p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith(@"\??\", StringComparison.Ordinal)) p = p.Substring(4);
            int guid = p.LastIndexOf("#{", StringComparison.Ordinal);
            if (guid > 0) p = p.Substring(0, guid);
            return p.Replace('#', '\\');
        }

        private IntPtr Proc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == Native.WM_INPUT)
            {
                int size = 0;
                int header = Marshal.SizeOf<Native.RAWINPUTHEADER>();
                Native.GetRawInputData(lParam, Native.RID_INPUT, null, ref size, header);
                if (size > 0 && size < 4096)
                {
                    byte* buf = stackalloc byte[size];
                    if (Native.GetRawInputData(lParam, Native.RID_INPUT, buf, ref size, header) > 0)
                    {
                        var h = *(Native.RAWINPUTHEADER*)buf;
                        string id = InstanceOf(h.hDevice);
                        if (h.dwType == Native.RIM_TYPEKEYBOARD)
                        {
                            var k = *(Native.RAWKEYBOARD*)(buf + header);
                            _events.Enqueue(new RawEvent(id, true, k.VKey, k.MakeCode, (k.Flags & 0x01) != 0, 0, 0));
                        }
                        else if (h.dwType == Native.RIM_TYPEMOUSE)
                        {
                            var m = *(Native.RAWMOUSE*)(buf + header);
                            // usFlags bit 0 set is absolute movement, which
                            // the probe's mouse never sends.
                            if ((m.usFlags & 0x01) == 0)
                                _events.Enqueue(new RawEvent(id, false, 0, 0, false, m.lLastX, m.lLastY));
                        }
                    }
                }
            }
            else if (msg == Native.WM_CLOSE)
            {
                Native.DestroyWindow(hwnd);
                return IntPtr.Zero;
            }
            else if (msg == Native.WM_DESTROY)
            {
                Native.PostQuitMessage(0);
                return IntPtr.Zero;
            }
            return Native.DefWindowProcW(hwnd, msg, wParam, lParam);
        }

        public void Dispose()
        {
            if (_hwnd != IntPtr.Zero) Native.PostMessageW(_hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(2000);
            Native.UnregisterClassW(ClassName, Native.GetModuleHandleW(null));
        }
    }
}
