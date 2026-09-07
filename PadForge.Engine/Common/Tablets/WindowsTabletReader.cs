using System.Runtime.InteropServices;
using System.Text;

namespace PadForge.Engine.Tablets;

public sealed class WindowsTabletReader : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<string, WindowsTabletDevice> devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<IntPtr, string> rawPaths = new();
    private readonly Func<string, bool> excludeInstance;
    private readonly WindowProc callback;
    private Thread thread;
    private Timer timer;
    private Task enumeration = Task.CompletedTask;
    private volatile bool running;
    private bool disposed;
    private int enumerating;
    private uint threadId;
    private IntPtr window, notification, rawBuffer;
    private uint rawBufferLength;

    public event Action DevicesChanged;
    public event Action<WindowsTabletDevice, int, bool, string> CaptureChanged;

    public WindowsTabletReader(Func<string, bool> shouldExcludeInstance = null)
    {
        excludeInstance = shouldExcludeInstance;
        callback = ProcessMessage;
    }

    public void Start()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (running) return;
            running = true;
            thread = new Thread(MessageLoop) { IsBackground = true, Name = "Tablet-Input" };
            thread.Start();
        }
    }

    public WindowsTabletDevice[] GetDevices()
    {
        lock (gate) return devices.Values.ToArray();
    }

    private void ScheduleEnumeration()
    {
        if (!running || Interlocked.CompareExchange(ref enumerating, 1, 0) != 0) return;
        enumeration = Task.Run(() =>
        {
            try
            {
                var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in TabletNative.EnumerateInterfaces())
                {
                    if (!running) return;
                    present.Add(item.Path);
                    lock (gate) { if (devices.ContainsKey(item.Path)) continue; }
                    TabletReportDescriptor descriptor = null;
                    try
                    {
                        descriptor = TabletReportDescriptor.TryOpen(item.Path, item.Instance);
                        if (descriptor == null) continue;
                        if (excludeInstance?.Invoke(item.Instance) == true) continue;
                        var device = new WindowsTabletDevice(descriptor, OnCaptureChanged);
                        lock (gate)
                        {
                            if (!running || devices.ContainsKey(item.Path)) { device.Dispose(); continue; }
                            devices.Add(item.Path, device);
                            descriptor = null;
                        }
                        SdlDiagLog.WriteLine($"TABLET + {device.VendorId:X4}:{device.ProductId:X4} {device.Name} pressure={device.TouchpadPressureSupported} path={device.DevicePath}");
                        DevicesChanged?.Invoke();
                    }
                    catch (Exception error) { SdlDiagLog.WriteLine($"TABLET metadata failed: {item.Path}: {error.Message}"); }
                    finally { descriptor?.Dispose(); }
                }
                List<WindowsTabletDevice> removed = new();
                WindowsTabletDevice[] candidates;
                lock (gate) candidates = devices.Values.Where(d => !present.Contains(d.DevicePath)).ToArray();
                // A stopped collection can still be connected. Keep its failure status and unhide control.
                var absent = candidates.Where(d => !TabletNative.IsPresent(d.DeviceInstanceId)).ToArray();
                lock (gate)
                {
                    if (!running) return;
                    foreach (var device in absent)
                    {
                        if (device.CaptureState == TabletCaptureState.Switching
                            || !devices.TryGetValue(device.DevicePath, out var current) || !ReferenceEquals(current, device)) continue;
                        devices.Remove(device.DevicePath);
                        removed.Add(device);
                    }
                }
                foreach (var device in removed)
                {
                    device.Dispose();
                    SdlDiagLog.WriteLine($"TABLET - {device.DevicePath}");
                }
                if (removed.Count > 0) DevicesChanged?.Invoke();
            }
            catch (Exception error) { if (running) SdlDiagLog.WriteLine($"TABLET enumeration failed: {error.Message}"); }
            finally { Volatile.Write(ref enumerating, 0); }
        });
    }

    private void OnCaptureChanged(WindowsTabletDevice device, int version, bool rollback, string error)
    {
        if (!running) return;
        CaptureChanged?.Invoke(device, version, rollback, error);
        DevicesChanged?.Invoke();
    }

    private void MessageLoop()
    {
        string className = "PadForge.Tablet." + Guid.NewGuid().ToString("N");
        IntPtr module = GetModuleHandle(null);
        threadId = GetCurrentThreadId();
        var wc = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(),
            Instance = module,
            Proc = Marshal.GetFunctionPointerForDelegate(callback),
            ClassName = className
        };
        try
        {
            if (RegisterClassEx(ref wc) == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            window = CreateWindowEx(0, className, "", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, module, IntPtr.Zero);
            if (window == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var registration = new[] { new RawDevice(13, 1, window), new RawDevice(13, 2, window) };
            if (!RegisterRawInputDevices(registration, 2, (uint)Marshal.SizeOf<RawDevice>())) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var filter = new NotificationFilter { Size = Marshal.SizeOf<NotificationFilter>(), Type = 5, ClassGuid = TabletNative.HidInterface };
            notification = RegisterDeviceNotification(window, ref filter, 0);
            timer = new Timer(_ => ScheduleEnumeration(), null, 0, 2000);
            while (running && GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        catch (Exception error) { SdlDiagLog.WriteLine($"TABLET listener failed: {error.Message}"); }
        finally
        {
            timer?.Dispose();
            timer = null;
            if (notification != IntPtr.Zero) UnregisterDeviceNotification(notification);
            notification = IntPtr.Zero;
            if (window != IntPtr.Zero)
            {
                var remove = new[] { new RawDevice(13, 1, IntPtr.Zero) { Flags = 1 }, new RawDevice(13, 2, IntPtr.Zero) { Flags = 1 } };
                RegisterRawInputDevices(remove, 2, (uint)Marshal.SizeOf<RawDevice>());
                DestroyWindow(window);
            }
            window = IntPtr.Zero;
            UnregisterClass(className, module);
            if (rawBuffer != IntPtr.Zero) Marshal.FreeHGlobal(rawBuffer);
            rawBuffer = IntPtr.Zero;
            rawBufferLength = 0;
            threadId = 0;
        }
    }

    private IntPtr ProcessMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (running && message == 0xFF) ReadRaw(lParam);
            else if (running && message is 0xFE or 0x219)
            {
                if (message == 0xFE && wParam == new IntPtr(2))
                {
                    // HidHide removes Raw Input visibility while the HID collection remains connected.
                    rawPaths.Remove(lParam);
                }
                ScheduleEnumeration();
            }
        }
        catch (Exception error) { SdlDiagLog.WriteLine($"TABLET input failed: {error.Message}"); }
        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    private void ReadRaw(IntPtr input)
    {
        uint length = 0, headerLength = (uint)Marshal.SizeOf<RawHeader>();
        if (GetRawInputData(input, 0x10000003, IntPtr.Zero, ref length, headerLength) != 0 || length < headerLength + 8 || length > 1024 * 1024) return;
        if (length > rawBufferLength)
        {
            if (rawBuffer != IntPtr.Zero) Marshal.FreeHGlobal(rawBuffer);
            rawBuffer = Marshal.AllocHGlobal((int)length);
            rawBufferLength = length;
        }
        if (GetRawInputData(input, 0x10000003, rawBuffer, ref length, headerLength) != length) return;
        var header = Marshal.PtrToStructure<RawHeader>(rawBuffer);
        if (header.Type != 2) return;
        if (!rawPaths.TryGetValue(header.Device, out string path))
        {
            uint size = 1024;
            var name = new StringBuilder((int)size);
            if (GetRawInputDeviceInfo(header.Device, 0x20000007, name, ref size) == uint.MaxValue) return;
            rawPaths[header.Device] = path = name.ToString();
        }
        WindowsTabletDevice device;
        lock (gate) devices.TryGetValue(path, out device);
        if (device == null) return;
        int reportLength = Marshal.ReadInt32(rawBuffer, (int)headerLength);
        int count = Marshal.ReadInt32(rawBuffer, (int)headerLength + 4);
        if (reportLength <= 0 || count <= 0 || (long)reportLength * count > length - headerLength - 8) return;
        IntPtr data = rawBuffer + (int)headerLength + 8;
        for (int i = 0; i < count; i++) device.FeedRaw(data + i * reportLength, reportLength);
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            running = false;
        }
        timer?.Dispose();
        if (threadId != 0) PostThreadMessage(threadId, 0x12, IntPtr.Zero, IntPtr.Zero);
        WindowsTabletDevice[] old;
        lock (gate) { old = devices.Values.ToArray(); devices.Clear(); }
        foreach (var device in old) device.Dispose();
        thread?.Join(2000);
    }

    private delegate IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] private struct RawDevice
    {
        internal ushort Page, Usage;
        internal uint Flags;
        internal IntPtr Target;
        internal RawDevice(ushort page, ushort usage, IntPtr target) { Page = page; Usage = usage; Flags = 0x2100; Target = target; }
    }
    [StructLayout(LayoutKind.Sequential)] private struct RawHeader { internal uint Type, Size; internal IntPtr Device, Param; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass
    {
        internal uint Size, Style;
        internal IntPtr Proc;
        internal int ClassExtra, WindowExtra;
        internal IntPtr Instance, Icon, Cursor, Background;
        [MarshalAs(UnmanagedType.LPWStr)] internal string Menu;
        [MarshalAs(UnmanagedType.LPWStr)] internal string ClassName;
        internal IntPtr SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct WindowMessage { internal IntPtr Window; internal uint Id; internal IntPtr WParam, LParam; internal uint Time; internal int X, Y; internal uint Private; }
    [StructLayout(LayoutKind.Sequential)] private struct NotificationFilter { internal int Size, Type, Reserved; internal Guid ClassGuid; internal short Name; }
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClass value);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(uint exStyle, string className, string name, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr module, IntPtr data);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "GetMessageW")] private static extern int GetMessage(out WindowMessage message, IntPtr window, uint minimum, uint maximum);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TranslateMessage(ref WindowMessage message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] private static extern IntPtr DispatchMessage(ref WindowMessage message);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "UnregisterClassW", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnregisterClass(string name, IntPtr module);
    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostThreadMessage(uint id, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RegisterRawInputDevices(RawDevice[] devices, uint count, uint size);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetRawInputData(IntPtr input, uint command, IntPtr data, ref uint size, uint headerSize);
    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, StringBuilder data, ref uint size);
    [DllImport("user32.dll", EntryPoint = "RegisterDeviceNotificationW", SetLastError = true)] private static extern IntPtr RegisterDeviceNotification(IntPtr window, ref NotificationFilter filter, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnregisterDeviceNotification(IntPtr notification);
}
