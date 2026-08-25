using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace PadForge.Common.Input
{
    /// <summary>One vendor-defined HID top-level collection (usage page
    /// 0xFF00 and up), the kind a handheld's firmware uses to report its
    /// hidden buttons (issue #343).</summary>
    internal sealed class VendorHidCollection
    {
        public string Path;
        /// <summary>VID:PID:PAGE:USAGE, the identity a definition is filed
        /// under. Stable across reboots and between two machines of one
        /// model, unlike the interface path.</summary>
        public string Key;
        public string Name;
        public ushort VendorId;
        public ushort ProductId;
        public ushort UsagePage;
        public ushort Usage;
        public ushort InputReportLength;
    }

    /// <summary>
    /// Enumerates vendor-defined HID collections (issue #343). No VID/PID
    /// list: every present HID interface whose top-level usage page is in
    /// the vendor range is a candidate, which is what lets a handheld
    /// released tomorrow be learned. Per-path verdicts are cached the way
    /// the headset runtime caches its marker probe, so the descriptor read
    /// runs once per appearance. Blocking device I/O: sweep worker only.
    /// </summary>
    internal static class VendorHidRuntime
    {
        private const ushort VendorPageMin = 0xFF00;

        private static readonly Dictionary<string, VendorHidCollection> _verdicts =
            new(StringComparer.OrdinalIgnoreCase);

        internal static string MakeKey(ushort vid, ushort pid, ushort page, ushort usage) =>
            $"{vid:X4}:{pid:X4}:{page:X4}:{usage:X4}";

        internal static void InvalidateCache()
        {
            lock (_verdicts) _verdicts.Clear();
        }

        /// <summary>Present vendor collections, or null when enumeration
        /// itself failed (kept distinct from "none present" so a transient
        /// SetupAPI failure never churns open readers).</summary>
        internal static List<VendorHidCollection> Enumerate()
        {
            var result = new List<VendorHidCollection>();
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
                    string path = GetInterfacePath(set, ref iface);
                    if (string.IsNullOrEmpty(path)) continue;
                    present.Add(path);

                    VendorHidCollection verdict;
                    bool known;
                    lock (_verdicts) known = _verdicts.TryGetValue(path, out verdict);
                    if (!known)
                    {
                        verdict = Probe(path);
                        lock (_verdicts) _verdicts[path] = verdict;
                    }
                    if (verdict != null) result.Add(verdict);
                }
            }
            finally
            {
                SonyHeadsetHid.SetupDiDestroyDeviceInfoList(set);
            }

            lock (_verdicts)
            {
                List<string> gone = null;
                foreach (var key in _verdicts.Keys)
                    if (!present.Contains(key)) (gone ??= new List<string>()).Add(key);
                if (gone != null) foreach (var key in gone) _verdicts.Remove(key);
            }
            return result;
        }

        private static string GetInterfacePath(IntPtr set, ref SonyHeadsetHid.SP_DEVICE_INTERFACE_DATA iface)
        {
            SonyHeadsetHid.SetupDiGetDeviceInterfaceDetail(set, ref iface, IntPtr.Zero, 0, out uint needed, IntPtr.Zero);
            if (needed == 0 || needed > 4096) return null;
            IntPtr detail = Marshal.AllocHGlobal((int)needed);
            try
            {
                Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                if (!SonyHeadsetHid.SetupDiGetDeviceInterfaceDetail(set, ref iface, detail, needed, out _, IntPtr.Zero))
                    return null;
                return Marshal.PtrToStringUni(detail + 4);
            }
            finally
            {
                Marshal.FreeHGlobal(detail);
            }
        }

        /// <summary>Query-only open (no read access needed for the
        /// descriptor), so a collection another program holds exclusively
        /// is still listed and named. Returns null for a non-vendor page.</summary>
        private static VendorHidCollection Probe(string path)
        {
            var handle = SonyHeadsetHid.CreateFile(path, 0,
                SonyHeadsetHid.FILE_SHARE_READ | SonyHeadsetHid.FILE_SHARE_WRITE,
                IntPtr.Zero, SonyHeadsetHid.OPEN_EXISTING, 0, IntPtr.Zero);
            if (handle.IsInvalid) { handle.Dispose(); return null; }
            IntPtr preparsed = IntPtr.Zero;
            try
            {
                if (!SonyHeadsetHid.HidD_GetPreparsedData(handle, out preparsed)) return null;
                if (SonyHeadsetHid.HidP_GetCaps(preparsed, out var caps) != SonyHeadsetHid.HIDP_STATUS_SUCCESS)
                    return null;
                if (caps.UsagePage < VendorPageMin) return null;
                // A collection with no input report carries no button.
                if (caps.InputReportByteLength == 0) return null;

                var attributes = new SonyHeadsetHid.HIDD_ATTRIBUTES
                {
                    Size = Marshal.SizeOf<SonyHeadsetHid.HIDD_ATTRIBUTES>()
                };
                SonyHeadsetHid.HidD_GetAttributes(handle, ref attributes);
                string name = ReadProductString(handle);
                if (string.IsNullOrWhiteSpace(name))
                    name = $"HID {attributes.VendorID:X4}:{attributes.ProductID:X4}";
                return new VendorHidCollection
                {
                    Path = path,
                    Key = MakeKey(attributes.VendorID, attributes.ProductID, caps.UsagePage, caps.Usage),
                    Name = name,
                    VendorId = attributes.VendorID,
                    ProductId = attributes.ProductID,
                    UsagePage = caps.UsagePage,
                    Usage = caps.Usage,
                    InputReportLength = caps.InputReportByteLength,
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

        private static string ReadProductString(SafeFileHandle handle)
        {
            var buffer = new byte[512];
            if (!SonyHeadsetHid.HidD_GetProductString(handle, buffer, (uint)buffer.Length)) return null;
            string s = System.Text.Encoding.Unicode.GetString(buffer);
            int nul = s.IndexOf('\0');
            return (nul >= 0 ? s.Substring(0, nul) : s).Trim();
        }
    }

    /// <summary>
    /// Reads one vendor collection's input reports on its own thread and
    /// hands each one to a listener (issue #343). Overlapped reads with
    /// bounded waits, the headset reader's shape, so teardown is never
    /// stranded behind a silent device. Shared-read open: the vendor's own
    /// daemon may hold the same collection and both keep receiving.
    /// </summary>
    internal sealed class VendorHidReader : IDisposable
    {
        public VendorHidCollection Collection { get; }

        /// <summary>A report arrived: the reader's buffer (byte 0 is the
        /// report id, 0 when the collection has none) and its length. The
        /// buffer is reused; a listener that keeps it must copy.</summary>
        public event Action<VendorHidReader, byte[], int> ReportReceived;

        private SafeFileHandle _handle;
        private Thread _reader;
        private ManualResetEvent _readEvent;
        private volatile bool _attached;
        private volatile bool _disposed;
        private long _reports;

        public VendorHidReader(VendorHidCollection collection)
        {
            Collection = collection;
        }

        public bool IsAttached => _attached && !_disposed;
        public long ReportCount => Interlocked.Read(ref _reports);

        public bool Open()
        {
            if (_disposed) return false;
            var handle = SonyHeadsetHid.CreateFile(Collection.Path, SonyHeadsetHid.GENERIC_READ,
                SonyHeadsetHid.FILE_SHARE_READ | SonyHeadsetHid.FILE_SHARE_WRITE,
                IntPtr.Zero, SonyHeadsetHid.OPEN_EXISTING,
                SonyHeadsetHid.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (handle.IsInvalid) { handle.Dispose(); return false; }
            _handle = handle;
            _readEvent = new ManualResetEvent(false);
            _attached = true;
            try
            {
                _reader = new Thread(ReaderLoop) { IsBackground = true, Name = "PadForge.VendorHid" };
                _reader.Start();
                return true;
            }
            catch
            {
                _attached = false;
                _handle = null;
                _readEvent.Dispose();
                _readEvent = null;
                handle.Dispose();
                return false;
            }
        }

        private void ReaderLoop()
        {
            int len = Math.Max(Collection.InputReportLength, (ushort)1);
            var report = new byte[len];
            var reportPin = GCHandle.Alloc(report, GCHandleType.Pinned);
            IntPtr overlappedPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>());
            bool ioPending = false;
            try
            {
                var overlapped = new NativeOverlapped
                {
                    EventHandle = _readEvent.SafeWaitHandle.DangerousGetHandle()
                };
                try
                {
                    while (_attached && !_disposed)
                    {
                        _readEvent.Reset();
                        Marshal.StructureToPtr(overlapped, overlappedPtr, false);
                        ioPending = false;
                        if (!SonyHeadsetHid.ReadFile(_handle, reportPin.AddrOfPinnedObject(),
                                (uint)report.Length, out uint bytes, overlappedPtr))
                        {
                            if (Marshal.GetLastWin32Error() != SonyHeadsetHid.ERROR_IO_PENDING)
                                break;
                            ioPending = true;
                        }
                        while (_attached && !_disposed)
                        {
                            if (_readEvent.WaitOne(100)) break;
                        }
                        if (!_attached || _disposed)
                        {
                            DrainPendingRead(overlappedPtr, ref ioPending);
                            break;
                        }
                        if (!SonyHeadsetHid.GetOverlappedResult(_handle, overlappedPtr, out bytes, false))
                        {
                            DrainPendingRead(overlappedPtr, ref ioPending);
                            break;
                        }
                        ioPending = false;
                        if (bytes == 0) continue;
                        Interlocked.Increment(ref _reports);
                        try { ReportReceived?.Invoke(this, report, (int)bytes); }
                        catch { }
                    }
                }
                catch
                {
                    // A vanishing device surfaces as a throw from any native
                    // call; the sweep retires the reader off IsAttached.
                }
            }
            finally
            {
                DrainPendingRead(overlappedPtr, ref ioPending);
                Marshal.FreeHGlobal(overlappedPtr);
                reportPin.Free();
                _attached = false;
            }
        }

        private void DrainPendingRead(IntPtr overlappedPtr, ref bool ioPending)
        {
            if (!ioPending) return;
            try
            {
                var handle = _handle;
                if (handle != null && !handle.IsInvalid)
                {
                    SonyHeadsetHid.CancelIoEx(handle, overlappedPtr);
                    SonyHeadsetHid.GetOverlappedResult(handle, overlappedPtr, out _, true);
                }
            }
            catch { }
            ioPending = false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _attached = false;
            var reader = _reader;
            _reader = null;
            bool joined = true;
            if (reader != null)
            {
                try { if (_handle != null && !_handle.IsInvalid) SonyHeadsetHid.CancelIoEx(_handle, IntPtr.Zero); } catch { }
                try { _readEvent?.Set(); } catch { }
                joined = reader.Join(2000);
            }
            // The reader still inside a native call owns the handle and the
            // event; leak them rather than free under it (headset rule).
            if (!joined) return;
            _handle?.Dispose();
            _handle = null;
            _readEvent?.Dispose();
            _readEvent = null;
        }
    }
}
