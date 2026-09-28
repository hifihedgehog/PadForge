using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using PadForge.Engine.Common.AnalogKeyboard;

namespace PadForge.Common.Input
{
    /// <summary>One HID collection the sweep found at least one analog
    /// keyboard route for (issue #468).</summary>
    internal sealed class AnalogKeyboardCandidate
    {
        /// <summary>The collection's metadata, its siblings included.</summary>
        public AnalogKeyboardDeviceInfo Info = new();

        /// <summary>The routes whose metadata test accepted it, in priority
        /// order. The device tries each until a handshake succeeds.</summary>
        public List<AnalogKeyboardRoute> Routes = new();

        /// <summary>The name before any handshake: the first route's model
        /// name, else the product string.</summary>
        public string Name = string.Empty;

        /// <summary>What the keyboard's identity is filed under: vendor,
        /// product with Wooting's mode bits masked, and the serial number, or
        /// the container ID or collection path for a keyboard with no serial.
        /// Stable across USB ports for a keyboard that reports a serial, and
        /// across a Wooting's gamepad modes.</summary>
        public string IdentityKey = string.Empty;

        public string Path => Info.Path;
        public ushort VendorId => Info.VendorId;
        public ushort ProductId => Info.ProductId;
        public string Serial => Info.SerialNumber;

        /// <summary>The first route's protocol, for logs.</summary>
        public AnalogKeyboardProtocol Protocol => Routes.Count > 0 ? Routes[0].Protocol : AnalogKeyboardProtocol.None;

        /// <summary>Priority of the first route, for ordering.</summary>
        internal int Priority;
    }

    /// <summary>
    /// Finds the analog keyboards on the HID tree (issue #468). Every present
    /// HID collection is opened for a query-only look at its attributes,
    /// caps, strings, declared report IDs and container, the facts the routes
    /// decide on (<see cref="AnalogKeyboardRoutes.Candidates"/>). Nothing is
    /// written here. Per-path metadata is cached the way
    /// <see cref="VendorHidRuntime"/> caches its verdicts, so the probe runs
    /// once per appearance. Blocking device I/O: sweep worker only.
    ///
    /// <para>The probe is Soup's hwHid::getAll on Windows (HidD_GetAttributes,
    /// HidP_GetCaps, HidP_InitializeReportForID for hwHid::hasReportId), with
    /// the container ID HallJoy's RongYuan stream route pairs collections by.</para>
    /// </summary>
    internal static class AnalogKeyboardHidRuntime
    {
        private static readonly Dictionary<string, AnalogKeyboardDeviceInfo> _probed =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _unreadable = new(StringComparer.OrdinalIgnoreCase);

        internal static void InvalidateCache()
        {
            lock (_probed)
            {
                _probed.Clear();
                _unreadable.Clear();
            }
        }

        /// <summary>Present analog keyboard collections in route priority
        /// order, or null when enumeration itself failed (kept distinct from
        /// "none" so a transient SetupAPI failure never retires open rows).</summary>
        internal static List<AnalogKeyboardCandidate> Enumerate()
        {
            var infos = new List<AnalogKeyboardDeviceInfo>();
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            SonyHeadsetHid.HidD_GetHidGuid(out Guid hidGuid);
            IntPtr set = SonyHeadsetHid.SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero,
                SonyHeadsetHid.DIGCF_PRESENT | SonyHeadsetHid.DIGCF_DEVICEINTERFACE);
            if (set == IntPtr.Zero || set == new IntPtr(-1)) return null;
            try
            {
                var iface = new SonyHeadsetHid.SP_DEVICE_INTERFACE_DATA
                {
                    cbSize = Marshal.SizeOf<SonyHeadsetHid.SP_DEVICE_INTERFACE_DATA>()
                };
                for (uint index = 0; SonyHeadsetHid.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero,
                        ref hidGuid, index, ref iface); index++)
                {
                    string path = GetInterfacePath(set, ref iface, out uint devInst);
                    if (string.IsNullOrEmpty(path)) continue;
                    present.Add(path);

                    AnalogKeyboardDeviceInfo info;
                    bool known, unreadable;
                    lock (_probed)
                    {
                        known = _probed.TryGetValue(path, out info);
                        unreadable = _unreadable.Contains(path);
                    }
                    if (unreadable) continue;
                    if (!known)
                    {
                        info = Probe(path, devInst);
                        lock (_probed)
                        {
                            if (info == null) _unreadable.Add(path);
                            else _probed[path] = info;
                        }
                    }
                    if (info != null) infos.Add(info);
                }
            }
            finally
            {
                SonyHeadsetHid.SetupDiDestroyDeviceInfoList(set);
            }

            lock (_probed)
            {
                Forget(_probed.Keys, present, key => _probed.Remove(key));
                Forget(_unreadable, present, key => _unreadable.Remove(key));
            }

            LinkSiblings(infos);
            var candidates = new List<AnalogKeyboardCandidate>();
            var order = AnalogKeyboardRoutes.All;
            foreach (var info in infos)
            {
                var routes = AnalogKeyboardRoutes.Candidates(info);
                if (routes.Count == 0) continue;
                candidates.Add(new AnalogKeyboardCandidate
                {
                    Info = info,
                    Routes = routes,
                    Name = NameFor(info, routes[0]),
                    IdentityKey = IdentityKeyFor(info),
                    Priority = IndexOf(order, routes[0]),
                });
            }
            // A Wooting's v2 interface ranks above its v1 interface, so the
            // sweep opens v2 and skips v1 as the same identity (the Wooting
            // SDK opens one analog interface per keyboard, and v2 carries the
            // 10-bit values and key namespaces).
            candidates.Sort((a, b) => a.Priority.CompareTo(b.Priority));
            return candidates;
        }

        private static void Forget(IEnumerable<string> keys, HashSet<string> present, Action<string> remove)
        {
            List<string> gone = null;
            foreach (var key in keys)
                if (!present.Contains(key)) (gone ??= new List<string>()).Add(key);
            if (gone != null) foreach (var key in gone) remove(key);
        }

        private static int IndexOf(IReadOnlyList<AnalogKeyboardRoute> order, AnalogKeyboardRoute route)
        {
            for (int i = 0; i < order.Count; i++)
                if (ReferenceEquals(order[i], route)) return i;
            return order.Count;
        }

        /// <summary>Fills each collection's siblings: the other collections
        /// of the same VID, PID and container.</summary>
        internal static void LinkSiblings(List<AnalogKeyboardDeviceInfo> infos)
        {
            var groups = new Dictionary<string, List<AnalogKeyboardDeviceInfo>>(StringComparer.OrdinalIgnoreCase);
            foreach (var info in infos)
            {
                if (string.IsNullOrEmpty(info.ContainerId)) continue;
                string key = $"{info.VendorId:X4}:{info.ProductId:X4}:{info.ContainerId}";
                if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<AnalogKeyboardDeviceInfo>();
                list.Add(info);
            }
            foreach (var list in groups.Values)
                foreach (var info in list)
                    info.Siblings = list.FindAll(other => !ReferenceEquals(other, info));
        }

        internal static string NameFor(AnalogKeyboardDeviceInfo info, AnalogKeyboardRoute route)
        {
            string name = null;
            try { name = route.Name?.Invoke(info); }
            catch { name = null; }
            if (!string.IsNullOrWhiteSpace(name)) return name;
            return string.IsNullOrWhiteSpace(info.ProductString)
                ? $"Analog keyboard {info.VendorId:X4}:{info.ProductId:X4}"
                : info.ProductString.Trim();
        }

        internal static string IdentityKeyFor(AnalogKeyboardDeviceInfo info)
        {
            ushort identityPid = AnalogKeyboardCatalog.IdentityProductId(info.VendorId, info.ProductId);
            string tail = !string.IsNullOrWhiteSpace(info.SerialNumber) ? info.SerialNumber.Trim()
                : !string.IsNullOrEmpty(info.ContainerId) ? info.ContainerId
                : info.Path.ToLowerInvariant();
            return $"{info.VendorId:X4}:{identityPid:X4}:{tail}";
        }

        /// <summary>USB paths carry "vid_XXXX", Bluetooth paths
        /// "_vid&amp;0002XXXX".</summary>
        internal static bool TryVendorFromPath(string path, out ushort vid)
        {
            vid = 0;
            if (string.IsNullOrEmpty(path)) return false;
            int at = path.IndexOf("vid_", StringComparison.OrdinalIgnoreCase);
            if (at >= 0 && at + 8 <= path.Length)
                return ushort.TryParse(path.AsSpan(at + 4, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out vid);
            at = path.IndexOf("_vid&", StringComparison.OrdinalIgnoreCase);
            if (at >= 0 && at + 13 <= path.Length)
                return ushort.TryParse(path.AsSpan(at + 9, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out vid);
            return false;
        }

        /// <summary>The USB interface number a path names ("mi_02"), or -1.</summary>
        internal static int InterfaceFromPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return -1;
            int at = path.IndexOf("mi_", StringComparison.OrdinalIgnoreCase);
            if (at < 0 || at + 5 > path.Length) return -1;
            return int.TryParse(path.AsSpan(at + 3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int mi)
                ? mi : -1;
        }

        private static string GetInterfacePath(IntPtr set, ref SonyHeadsetHid.SP_DEVICE_INTERFACE_DATA iface, out uint devInst)
        {
            devInst = 0;
            var devInfo = new SonyHeadsetHid.SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SonyHeadsetHid.SP_DEVINFO_DATA>() };
            SonyHeadsetHid.SetupDiGetDeviceInterfaceDetail(set, ref iface, IntPtr.Zero, 0, out uint needed, ref devInfo);
            if (needed == 0 || needed > 4096) return null;
            IntPtr detail = Marshal.AllocHGlobal((int)needed);
            try
            {
                Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                if (!SonyHeadsetHid.SetupDiGetDeviceInterfaceDetail(set, ref iface, detail, needed, out _, ref devInfo))
                    return null;
                devInst = devInfo.DevInst;
                return Marshal.PtrToStringUni(detail + 4);
            }
            finally
            {
                Marshal.FreeHGlobal(detail);
            }
        }

        /// <summary>Query-only open, so a collection another program holds is
        /// still described. Null when the collection cannot be read at all.</summary>
        private static AnalogKeyboardDeviceInfo Probe(string path, uint devInst)
        {
            var handle = SonyHeadsetHid.CreateFile(path, 0,
                SonyHeadsetHid.FILE_SHARE_READ | SonyHeadsetHid.FILE_SHARE_WRITE,
                IntPtr.Zero, SonyHeadsetHid.OPEN_EXISTING, 0, IntPtr.Zero);
            if (handle.IsInvalid) { handle.Dispose(); return null; }
            IntPtr preparsed = IntPtr.Zero;
            try
            {
                var attributes = new SonyHeadsetHid.HIDD_ATTRIBUTES
                {
                    Size = Marshal.SizeOf<SonyHeadsetHid.HIDD_ATTRIBUTES>()
                };
                if (!SonyHeadsetHid.HidD_GetAttributes(handle, ref attributes)) return null;
                if (!SonyHeadsetHid.HidD_GetPreparsedData(handle, out preparsed)) return null;
                if (SonyHeadsetHid.HidP_GetCaps(preparsed, out var caps) != SonyHeadsetHid.HIDP_STATUS_SUCCESS)
                    return null;

                // The report ID answers are taken now, while the preparsed
                // data exists, so the info needs no handle later.
                var input = ReportIds(HidP_Input, preparsed, caps.InputReportByteLength);
                var output = ReportIds(HidP_Output, preparsed, caps.OutputReportByteLength);
                var feature = ReportIds(HidP_Feature, preparsed, caps.FeatureReportByteLength);

                return new AnalogKeyboardDeviceInfo
                {
                    Path = path,
                    VendorId = attributes.VendorID,
                    ProductId = attributes.ProductID,
                    VersionNumber = attributes.VersionNumber,
                    UsagePage = caps.UsagePage,
                    Usage = caps.Usage,
                    InterfaceNumber = InterfaceFromPath(path),
                    InputReportLength = caps.InputReportByteLength,
                    OutputReportLength = caps.OutputReportByteLength,
                    FeatureReportLength = caps.FeatureReportByteLength,
                    ProductString = VendorHidRuntime.ReadProductString(handle) ?? string.Empty,
                    ManufacturerString = ReadString(handle, HidD_GetManufacturerString),
                    SerialNumber = ReadString(handle, HidD_GetSerialNumberString),
                    ContainerId = ContainerId(devInst),
                    HasInputReport = id => input.Contains(id),
                    HasOutputReport = id => output.Contains(id),
                    HasFeatureReport = id => feature.Contains(id),
                    InputValueCaps = ValueCaps(preparsed, caps.NumberInputValueCaps),
                };
            }
            catch
            {
                return null;
            }
            finally
            {
                if (preparsed != IntPtr.Zero) SonyHeadsetHid.HidD_FreePreparsedData(preparsed);
                handle.Dispose();
            }
        }

        /// <summary>Every report ID (0 to 255) the collection declares for a
        /// report type, the question HidP_InitializeReportForID answers.</summary>
        private static HashSet<byte> ReportIds(int type, IntPtr preparsed, ushort length)
        {
            var ids = new HashSet<byte>();
            if (length == 0) return ids;
            var scratch = new byte[length];
            for (int id = 0; id < 256; id++)
                if (HidP_InitializeReportForID(type, (byte)id, preparsed, scratch, length) == SonyHeadsetHid.HIDP_STATUS_SUCCESS)
                    ids.Add((byte)id);
            return ids;
        }

        private static IReadOnlyList<AnalogKeyboardValueCap> ValueCaps(IntPtr preparsed, ushort count)
        {
            if (count == 0) return Array.Empty<AnalogKeyboardValueCap>();
            var caps = new SonyHeadsetHid.HIDP_VALUE_CAPS[count];
            ushort n = count;
            if (SonyHeadsetHid.HidP_GetValueCaps(HidP_Input, caps, ref n, preparsed) != SonyHeadsetHid.HIDP_STATUS_SUCCESS)
                return Array.Empty<AnalogKeyboardValueCap>();
            var result = new AnalogKeyboardValueCap[n];
            for (int i = 0; i < n; i++)
                result[i] = new AnalogKeyboardValueCap(caps[i].ReportID, caps[i].UsagePage, caps[i].BitSize, caps[i].ReportCount);
            return result;
        }

        private delegate bool StringGetter(SafeFileHandle handle, byte[] buffer, uint bufferLength);

        private static string ReadString(SafeFileHandle handle, StringGetter getter)
        {
            var buffer = new byte[512];
            if (!getter(handle, buffer, (uint)buffer.Length)) return string.Empty;
            string s = System.Text.Encoding.Unicode.GetString(buffer);
            int nul = s.IndexOf('\0');
            return (nul >= 0 ? s.Substring(0, nul) : s).Trim();
        }

        /// <summary>DEVPKEY_Device_ContainerId of the collection's devnode, as
        /// a lowercase GUID string, or empty.</summary>
        private static string ContainerId(uint devInst)
        {
            if (devInst == 0) return string.Empty;
            var key = new DEVPROPKEY { fmtid = new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), pid = 2 };
            var buffer = new byte[16];
            uint size = (uint)buffer.Length;
            if (CM_Get_DevNode_PropertyW(devInst, ref key, out uint type, buffer, ref size, 0) != 0) return string.Empty;
            if (type != DEVPROP_TYPE_GUID || size != 16) return string.Empty;
            var guid = new Guid(buffer);
            return guid == Guid.Empty ? string.Empty : guid.ToString("D");
        }

        /// <summary>True while Razer Synapse runs, the process check Soup's
        /// areRazerAnalogueReportsEnabled makes.</summary>
        internal static bool IsSynapseRunning()
        {
            foreach (string name in AnalogKeyboardCatalog.SynapseProcessNames)
            {
                System.Diagnostics.Process[] found = null;
                try
                {
                    found = System.Diagnostics.Process.GetProcessesByName(name);
                    if (found.Length > 0) return true;
                }
                catch { }
                finally
                {
                    if (found != null) foreach (var p in found) p.Dispose();
                }
            }
            return false;
        }

        internal const int HidP_Input = 0;
        internal const int HidP_Output = 1;
        internal const int HidP_Feature = 2;
        private const uint DEVPROP_TYPE_GUID = 0x0000000D;

        [StructLayout(LayoutKind.Sequential)]
        private struct DEVPROPKEY
        {
            public Guid fmtid;
            public uint pid;
        }

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DEVPROPKEY propertyKey,
            out uint propertyType, [Out] byte[] propertyBuffer, ref uint propertyBufferSize, uint flags);

        [DllImport("hid.dll")]
        internal static extern uint HidP_InitializeReportForID(int reportType, byte reportId,
            IntPtr preparsedData, byte[] report, uint reportLength);

        [DllImport("hid.dll")]
        internal static extern bool HidD_GetSerialNumberString(SafeFileHandle handle, byte[] buffer, uint bufferLength);

        [DllImport("hid.dll")]
        internal static extern bool HidD_GetManufacturerString(SafeFileHandle handle, byte[] buffer, uint bufferLength);

        [DllImport("hid.dll")]
        internal static extern bool HidD_FlushQueue(SafeFileHandle handle);

        [DllImport("hid.dll")]
        internal static extern bool HidD_SetNumInputBuffers(SafeFileHandle handle, uint numberBuffers);
    }

    /// <summary>
    /// The overlapped HID channel one analog keyboard is read and commanded
    /// over (issue #468). Soup's hwHid on Windows in shape: one read kept
    /// pending across calls so no report is lost between them, writes padded
    /// to the collection's output report length, and a stale-report discard
    /// before each request. Feature reports and control-transfer output
    /// reports go through DeviceIoControl on the same handle, as HallJoy's
    /// HidIoOperation does, so every wait is bounded and a timed-out request
    /// is canceled and drained before its buffer is reused. A route that
    /// commands one collection and reads another (HallJoy's RongYuan stream)
    /// gets a second, read-only handle for the reads.
    ///
    /// <para>Owned by one thread at a time: the sweep's worker during the
    /// handshake, then the reader thread. <see cref="Abort"/> is the one call
    /// another thread may make, and it only cancels.</para>
    /// </summary>
    internal sealed class AnalogKeyboardHidChannel : IAnalogKeyboardTransport
    {
        private const int WriteTimeoutMs = 1000;
        private const int IoctlTimeoutMs = 500;

        private const uint IOCTL_HID_SET_FEATURE = 0x000B0191;
        private const uint IOCTL_HID_GET_FEATURE = 0x000B0192;
        private const uint IOCTL_HID_SET_OUTPUT_REPORT = 0x000B0195;

        private readonly SafeFileHandle _handle;
        private readonly SafeFileHandle _readHandle;
        private readonly int _inputLength;
        private readonly int _outputLength;
        private readonly int _featureLength;
        private readonly byte[] _readBuffer;
        private GCHandle _readPin;
        private readonly IntPtr _readOverlapped;
        private readonly ManualResetEvent _readEvent = new(false);
        private bool _readPending;
        private readonly byte[] _writeBuffer;
        private GCHandle _writePin;
        private readonly IntPtr _writeOverlapped;
        private readonly ManualResetEvent _writeEvent = new(false);
        private readonly byte[] _ioctlBuffer;
        private GCHandle _ioctlPin;
        private readonly IntPtr _ioctlOverlapped;
        private readonly ManualResetEvent _ioctlEvent = new(false);
        private volatile bool _aborted;
        private bool _closed;

        private AnalogKeyboardHidChannel(SafeFileHandle handle, SafeFileHandle readHandle,
            int inputLength, int outputLength, int featureLength)
        {
            _handle = handle;
            _readHandle = readHandle ?? handle;
            _inputLength = inputLength;
            _outputLength = outputLength;
            _featureLength = featureLength;
            _readBuffer = new byte[Math.Max(inputLength, 1)];
            _readPin = GCHandle.Alloc(_readBuffer, GCHandleType.Pinned);
            _readOverlapped = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>());
            _writeBuffer = new byte[Math.Max(outputLength, 1)];
            _writePin = GCHandle.Alloc(_writeBuffer, GCHandleType.Pinned);
            _writeOverlapped = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>());
            _ioctlBuffer = new byte[Math.Max(Math.Max(featureLength, outputLength), 1)];
            _ioctlPin = GCHandle.Alloc(_ioctlBuffer, GCHandleType.Pinned);
            _ioctlOverlapped = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>());
        }

        public int InputLength => _inputLength;
        public int OutputLength => _outputLength;
        public int FeatureLength => _featureLength;

        /// <summary>Why the last <see cref="Open"/> failed, for the sweep's
        /// retry policy: 32 and 33 mean another program holds the collection.</summary>
        [ThreadStatic] internal static int LastOpenError;

        /// <summary>Opens <paramref name="info"/> the way
        /// <paramref name="route"/> asks, and the companion collection read
        /// only and shared when the route names one. Null when either open
        /// fails.</summary>
        internal static AnalogKeyboardHidChannel Open(AnalogKeyboardDeviceInfo info, AnalogKeyboardRoute route,
            AnalogKeyboardDeviceInfo companion)
        {
            LastOpenError = 0;
            uint access = SonyHeadsetHid.GENERIC_READ | (route.Writable ? SonyHeadsetHid.GENERIC_WRITE : 0);
            uint share = route.Exclusive ? 0u : SonyHeadsetHid.FILE_SHARE_READ | SonyHeadsetHid.FILE_SHARE_WRITE;
            var handle = SonyHeadsetHid.CreateFile(info.Path, access, share,
                IntPtr.Zero, SonyHeadsetHid.OPEN_EXISTING, SonyHeadsetHid.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                LastOpenError = Marshal.GetLastWin32Error();
                handle.Dispose();
                return null;
            }

            SafeFileHandle readHandle = null;
            int inputLength = info.InputReportLength;
            if (companion != null)
            {
                readHandle = SonyHeadsetHid.CreateFile(companion.Path, SonyHeadsetHid.GENERIC_READ,
                    SonyHeadsetHid.FILE_SHARE_READ | SonyHeadsetHid.FILE_SHARE_WRITE,
                    IntPtr.Zero, SonyHeadsetHid.OPEN_EXISTING, SonyHeadsetHid.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
                if (readHandle.IsInvalid)
                {
                    LastOpenError = Marshal.GetLastWin32Error();
                    readHandle.Dispose();
                    handle.Dispose();
                    return null;
                }
                inputLength = companion.InputReportLength;
            }

            if (route.InputBuffers > 0)
                AnalogKeyboardHidRuntime.HidD_SetNumInputBuffers(readHandle ?? handle, (uint)route.InputBuffers);
            AnalogKeyboardHidRuntime.HidD_FlushQueue(readHandle ?? handle);
            return new AnalogKeyboardHidChannel(handle, readHandle, inputLength,
                info.OutputReportLength, info.FeatureReportLength);
        }

        public int Receive(byte[] buffer, int timeoutMs)
        {
            if (_aborted || _closed) return -1;
            if (!_readPending)
            {
                _readEvent.Reset();
                Marshal.StructureToPtr(new NativeOverlapped
                {
                    EventHandle = _readEvent.SafeWaitHandle.DangerousGetHandle()
                }, _readOverlapped, false);
                if (SonyHeadsetHid.ReadFile(_readHandle, _readPin.AddrOfPinnedObject(),
                        (uint)_readBuffer.Length, out uint immediate, _readOverlapped))
                    return CopyOut(buffer, (int)immediate);
                if (Marshal.GetLastWin32Error() != SonyHeadsetHid.ERROR_IO_PENDING) return -1;
                _readPending = true;
            }

            // Wait in slices so an abort is seen within one of them.
            long deadline = Environment.TickCount64 + Math.Max(0, timeoutMs);
            while (true)
            {
                if (_aborted) return -1;
                long left = deadline - Environment.TickCount64;
                int slice = (int)Math.Clamp(left, 0, 100);
                if (_readEvent.WaitOne(slice)) break;
                if (left <= 0) return 0; // the read stays pending for the next call
            }
            _readPending = false;
            if (!SonyHeadsetHid.GetOverlappedResult(_readHandle, _readOverlapped, out uint bytes, false))
                return -1;
            return CopyOut(buffer, (int)bytes);
        }

        private int CopyOut(byte[] buffer, int bytes)
        {
            int n = Math.Min(Math.Min(bytes, _readBuffer.Length), buffer.Length);
            Array.Copy(_readBuffer, buffer, n);
            return n;
        }

        public void DiscardStale()
        {
            if (_aborted || _closed) return;
            // A read that already completed holds a report from before the
            // request: consume it so the next Receive waits for a fresh one.
            if (_readPending && _readEvent.WaitOne(0))
            {
                _readPending = false;
                SonyHeadsetHid.GetOverlappedResult(_readHandle, _readOverlapped, out _, false);
            }
            AnalogKeyboardHidRuntime.HidD_FlushQueue(_readHandle);
        }

        public bool Send(byte[] report)
        {
            if (_aborted || _closed || report == null || report.Length == 0) return false;
            // Windows takes exactly the output report length, zero-padded,
            // Soup's hwHid::sendReport padding and RawHidOutput's.
            int length = Math.Max(_outputLength, report.Length);
            if (length > _writeBuffer.Length) return false;
            Array.Clear(_writeBuffer);
            Array.Copy(report, _writeBuffer, report.Length);
            _writeEvent.Reset();
            Marshal.StructureToPtr(new NativeOverlapped
            {
                EventHandle = _writeEvent.SafeWaitHandle.DangerousGetHandle()
            }, _writeOverlapped, false);
            if (WriteFile(_handle, _writePin.AddrOfPinnedObject(), (uint)length, IntPtr.Zero, _writeOverlapped))
                return true;
            if (Marshal.GetLastWin32Error() != SonyHeadsetHid.ERROR_IO_PENDING) return false;
            if (!_writeEvent.WaitOne(WriteTimeoutMs))
            {
                // Cancel only requests the abort. The buffer and OVERLAPPED
                // belong to the kernel until the write completes, so block on
                // that before returning (the RawHidOutput drain).
                SonyHeadsetHid.CancelIoEx(_handle, _writeOverlapped);
                SonyHeadsetHid.GetOverlappedResult(_handle, _writeOverlapped, out _, true);
                return false;
            }
            return SonyHeadsetHid.GetOverlappedResult(_handle, _writeOverlapped, out _, false);
        }

        public bool SendOutputReport(byte[] report)
            => report != null && report.Length > 0 && report.Length <= Math.Max(_outputLength, 1)
               && Ioctl(IOCTL_HID_SET_OUTPUT_REPORT, report, _outputLength, false, out _);

        public bool SetFeature(byte[] report)
            => report != null && report.Length > 0 && report.Length <= Math.Max(_featureLength, 1)
               && Ioctl(IOCTL_HID_SET_FEATURE, report, _featureLength, false, out _);

        public int GetFeature(byte[] buffer)
        {
            if (buffer == null || buffer.Length == 0 || _featureLength == 0) return -1;
            var request = new byte[Math.Min(buffer.Length, _featureLength)];
            request[0] = buffer[0];
            if (!Ioctl(IOCTL_HID_GET_FEATURE, request, _featureLength, true, out int transferred)) return -1;
            int n = Math.Min(buffer.Length, _featureLength);
            Array.Copy(_ioctlBuffer, buffer, n);
            return transferred;
        }

        /// <summary>One overlapped HID IOCTL on the command handle with the
        /// report padded to <paramref name="length"/>. The same buffer goes
        /// in and, for a GET, comes back out, as HallJoy passes it. A request
        /// that outlives the timeout is canceled and drained, so the kernel
        /// never writes into a buffer the next call is filling.</summary>
        private bool Ioctl(uint code, byte[] report, int length, bool output, out int transferred)
        {
            transferred = 0;
            if (_aborted || _closed || length <= 0 || length > _ioctlBuffer.Length) return false;
            Array.Clear(_ioctlBuffer);
            Array.Copy(report, _ioctlBuffer, Math.Min(report.Length, length));
            _ioctlEvent.Reset();
            Marshal.StructureToPtr(new NativeOverlapped
            {
                EventHandle = _ioctlEvent.SafeWaitHandle.DangerousGetHandle()
            }, _ioctlOverlapped, false);
            IntPtr buffer = _ioctlPin.AddrOfPinnedObject();
            bool done = DeviceIoControl(_handle, code, buffer, (uint)length,
                output ? buffer : IntPtr.Zero, output ? (uint)length : 0, IntPtr.Zero, _ioctlOverlapped);
            if (!done && Marshal.GetLastWin32Error() != SonyHeadsetHid.ERROR_IO_PENDING) return false;
            if (!done && !_ioctlEvent.WaitOne(IoctlTimeoutMs))
            {
                SonyHeadsetHid.CancelIoEx(_handle, _ioctlOverlapped);
                SonyHeadsetHid.GetOverlappedResult(_handle, _ioctlOverlapped, out _, true);
                return false;
            }
            if (!SonyHeadsetHid.GetOverlappedResult(_handle, _ioctlOverlapped, out uint bytes, false)) return false;
            transferred = (int)bytes;
            return true;
        }

        /// <summary>From any thread: stop every wait and cancel the I/O. The
        /// owning thread sees the flag within one wait slice, leaves its loop
        /// and calls <see cref="Close"/>. The events are left to the kernel:
        /// signaling one by hand while its I/O is still pending would let the
        /// owner free a buffer the kernel still owns.</summary>
        internal void Abort()
        {
            _aborted = true;
            try { if (!_handle.IsInvalid) SonyHeadsetHid.CancelIoEx(_handle, IntPtr.Zero); } catch { }
            try
            {
                if (!ReferenceEquals(_readHandle, _handle) && !_readHandle.IsInvalid)
                    SonyHeadsetHid.CancelIoEx(_readHandle, IntPtr.Zero);
            }
            catch { }
        }

        /// <summary>Owning thread only: drains the pending read (the kernel
        /// owns its buffer until it completes), then frees everything.</summary>
        internal void Close()
        {
            if (_closed) return;
            _closed = true;
            try
            {
                if (_readPending)
                {
                    SonyHeadsetHid.CancelIoEx(_readHandle, _readOverlapped);
                    SonyHeadsetHid.GetOverlappedResult(_readHandle, _readOverlapped, out _, true);
                    _readPending = false;
                }
            }
            catch { }
            try { if (!ReferenceEquals(_readHandle, _handle)) _readHandle.Dispose(); } catch { }
            try { _handle.Dispose(); } catch { }
            if (_readPin.IsAllocated) _readPin.Free();
            if (_writePin.IsAllocated) _writePin.Free();
            if (_ioctlPin.IsAllocated) _ioctlPin.Free();
            Marshal.FreeHGlobal(_readOverlapped);
            Marshal.FreeHGlobal(_writeOverlapped);
            Marshal.FreeHGlobal(_ioctlOverlapped);
            _readEvent.Dispose();
            _writeEvent.Dispose();
            _ioctlEvent.Dispose();
        }

        // IntPtr buffer and OVERLAPPED, never marshaled arrays: the kernel
        // reads the buffer after the call returns ERROR_IO_PENDING
        // (RawHidOutput's declaration, for the same reason).
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteFile(SafeFileHandle handle, IntPtr buffer, uint bytesToWrite,
            IntPtr bytesWritten, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle handle, uint ioControlCode,
            IntPtr inBuffer, uint inBufferSize, IntPtr outBuffer, uint outBufferSize,
            IntPtr bytesReturned, IntPtr overlapped);
    }
}
