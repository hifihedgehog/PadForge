using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using PadForge.Engine.Common.AnalogKeyboard;

namespace PadForge.Common.Input
{
    /// <summary>One analog keyboard collection the sweep found (issue #468).</summary>
    internal sealed class AnalogKeyboardCandidate
    {
        public string Path;
        public AnalogKeyboardProtocol Protocol;
        public ushort VendorId;
        public ushort ProductId;
        public ushort UsagePage;
        public ushort Usage;
        public ushort InputReportLength;
        public ushort OutputReportLength;
        public string Name;
        public string Serial;

        /// <summary>What the keyboard's identity is filed under: vendor,
        /// product with Wooting's mode bits masked, and the serial number, or
        /// the collection path for a keyboard with no serial. Stable across
        /// USB ports for a keyboard that reports a serial, and across a
        /// Wooting's gamepad modes.</summary>
        public string IdentityKey;
    }

    /// <summary>
    /// Finds the analog keyboards on the HID tree (issue #468): every present
    /// HID interface whose vendor is one the catalog knows is opened for a
    /// query-only look at its attributes, usage page and input report IDs,
    /// the facts <see cref="AnalogKeyboardCatalog.Identify"/> decides on.
    /// Per-path verdicts are cached the way <see cref="VendorHidRuntime"/>
    /// caches them, so the probe runs once per appearance. Blocking device
    /// I/O: sweep worker only.
    ///
    /// <para>The probe is Soup's hwHid::getAll on Windows: HidD_GetAttributes,
    /// HidP_GetCaps, and HidP_InitializeReportForID for the report ID
    /// question (hwHid::hasReportId).</para>
    /// </summary>
    internal static class AnalogKeyboardHidRuntime
    {
        private static readonly HashSet<ushort> KnownVendors = new()
        {
            AnalogKeyboardCatalog.WootingVendorId,
            AnalogKeyboardCatalog.LegacyWootingVendorId,
            AnalogKeyboardCatalog.RazerVendorId,
            AnalogKeyboardCatalog.NuPhyVendorId,
            AnalogKeyboardCatalog.DrunkDeerVendorId,
            AnalogKeyboardCatalog.KeychronVendorId,
            AnalogKeyboardCatalog.LemokeyVendorId,
            AnalogKeyboardCatalog.MadlionsVendorId,
            AnalogKeyboardCatalog.BytechVendorId,
        };

        private static readonly Dictionary<string, AnalogKeyboardCandidate> _verdicts =
            new(StringComparer.OrdinalIgnoreCase);

        internal static void InvalidateCache()
        {
            lock (_verdicts) _verdicts.Clear();
        }

        /// <summary>Present analog keyboard collections, one per keyboard, or
        /// null when enumeration itself failed (kept distinct from "none" so a
        /// transient SetupAPI failure never retires open rows).</summary>
        internal static List<AnalogKeyboardCandidate> Enumerate()
        {
            var found = new List<AnalogKeyboardCandidate>();
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
                    string path = VendorHidRuntime.GetInterfacePath(set, ref iface);
                    if (string.IsNullOrEmpty(path)) continue;
                    present.Add(path);

                    AnalogKeyboardCandidate verdict;
                    bool known;
                    lock (_verdicts) known = _verdicts.TryGetValue(path, out verdict);
                    if (!known)
                    {
                        verdict = ShouldProbe(path) ? Probe(path) : null;
                        lock (_verdicts) _verdicts[path] = verdict;
                    }
                    if (verdict != null) found.Add(verdict);
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
            return PreferOnePerKeyboard(found);
        }

        /// <summary>A Wooting on current firmware can expose both analog
        /// interfaces. Both describe one keyboard, so the v2 interface wins:
        /// it carries 10-bit values and the key namespaces. The Wooting SDK
        /// opens whichever interface it meets first per device ID and skips
        /// the other, which is the same one-per-keyboard rule with an
        /// arbitrary winner.</summary>
        internal static List<AnalogKeyboardCandidate> PreferOnePerKeyboard(List<AnalogKeyboardCandidate> found)
        {
            var byIdentity = new Dictionary<string, AnalogKeyboardCandidate>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            foreach (var c in found)
            {
                if (!byIdentity.TryGetValue(c.IdentityKey, out var existing))
                {
                    byIdentity[c.IdentityKey] = c;
                    order.Add(c.IdentityKey);
                    continue;
                }
                if (existing.Protocol == AnalogKeyboardProtocol.WootingV1
                    && c.Protocol == AnalogKeyboardProtocol.WootingV2)
                    byIdentity[c.IdentityKey] = c;
            }
            var result = new List<AnalogKeyboardCandidate>(order.Count);
            foreach (var key in order) result.Add(byIdentity[key]);
            return result;
        }

        /// <summary>Skips the open when the path names a vendor the catalog
        /// does not know. A path that names no vendor is probed.</summary>
        internal static bool ShouldProbe(string path)
        {
            if (!TryVendorFromPath(path, out ushort vid)) return true;
            return KnownVendors.Contains(vid);
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

        /// <summary>Query-only open, so a collection another program holds is
        /// still identified. Null when the collection is not an analog
        /// keyboard the catalog knows.</summary>
        private static AnalogKeyboardCandidate Probe(string path)
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
                if (!KnownVendors.Contains(attributes.VendorID)) return null;
                if (!SonyHeadsetHid.HidD_GetPreparsedData(handle, out preparsed)) return null;
                if (SonyHeadsetHid.HidP_GetCaps(preparsed, out var caps) != SonyHeadsetHid.HIDP_STATUS_SUCCESS)
                    return null;
                if (caps.InputReportByteLength == 0) return null;

                IntPtr pp = preparsed;
                var scratch = new byte[caps.InputReportByteLength];
                bool HasInputReport(byte id)
                    => HidP_InitializeReportForID(HidP_Input, id, pp, scratch, (uint)scratch.Length)
                        == SonyHeadsetHid.HIDP_STATUS_SUCCESS;

                var protocol = AnalogKeyboardCatalog.Identify(attributes.VendorID, attributes.ProductID,
                    caps.UsagePage, caps.Usage, HasInputReport);
                if (protocol == AnalogKeyboardProtocol.None) return null;

                string product = VendorHidRuntime.ReadProductString(handle);
                string serial = ReadSerial(handle);
                string name = AnalogKeyboardCatalog.ModelName(protocol, attributes.VendorID, attributes.ProductID);
                if (string.IsNullOrWhiteSpace(name))
                    name = string.IsNullOrWhiteSpace(product)
                        ? $"Analog keyboard {attributes.VendorID:X4}:{attributes.ProductID:X4}"
                        : product;
                ushort identityPid = AnalogKeyboardCatalog.IdentityProductId(attributes.VendorID, attributes.ProductID);
                string identity = $"{attributes.VendorID:X4}:{identityPid:X4}:"
                    + (string.IsNullOrWhiteSpace(serial) ? path.ToLowerInvariant() : serial.Trim());
                return new AnalogKeyboardCandidate
                {
                    Path = path,
                    Protocol = protocol,
                    VendorId = attributes.VendorID,
                    ProductId = attributes.ProductID,
                    UsagePage = caps.UsagePage,
                    Usage = caps.Usage,
                    InputReportLength = caps.InputReportByteLength,
                    OutputReportLength = caps.OutputReportByteLength,
                    Name = name,
                    Serial = serial ?? string.Empty,
                    IdentityKey = identity,
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

        private static string ReadSerial(SafeFileHandle handle)
        {
            var buffer = new byte[256];
            if (!HidD_GetSerialNumberString(handle, buffer, (uint)buffer.Length)) return null;
            string s = System.Text.Encoding.Unicode.GetString(buffer);
            int nul = s.IndexOf('\0');
            return (nul >= 0 ? s.Substring(0, nul) : s).Trim();
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

        [DllImport("hid.dll")]
        internal static extern uint HidP_InitializeReportForID(int reportType, byte reportId,
            IntPtr preparsedData, byte[] report, uint reportLength);

        [DllImport("hid.dll")]
        internal static extern bool HidD_GetSerialNumberString(SafeFileHandle handle, byte[] buffer, uint bufferLength);

        [DllImport("hid.dll")]
        internal static extern bool HidD_FlushQueue(SafeFileHandle handle);
    }

    /// <summary>
    /// The overlapped HID channel one analog keyboard is read and polled over
    /// (issue #468). Soup's hwHid on Windows in shape: one read kept pending
    /// across calls so no report is lost between them, writes padded to the
    /// collection's output report length, and a stale-report discard before
    /// each request. Every wait is bounded, the <see cref="VendorHidReader"/>
    /// rule, so teardown never strands behind a silent device.
    ///
    /// <para>Owned by one reader thread. <see cref="Abort"/> is the one call
    /// another thread may make, and it only cancels.</para>
    /// </summary>
    internal sealed class AnalogKeyboardHidChannel : IAnalogKeyboardTransport
    {
        private const int WriteTimeoutMs = 1000;

        private readonly SafeFileHandle _handle;
        private readonly int _outputLength;
        private readonly byte[] _readBuffer;
        private GCHandle _readPin;
        private readonly IntPtr _readOverlapped;
        private readonly ManualResetEvent _readEvent = new(false);
        private bool _readPending;
        private readonly byte[] _writeBuffer;
        private GCHandle _writePin;
        private readonly IntPtr _writeOverlapped;
        private readonly ManualResetEvent _writeEvent = new(false);
        private volatile bool _aborted;
        private bool _closed;

        private AnalogKeyboardHidChannel(SafeFileHandle handle, int inputLength, int outputLength)
        {
            _handle = handle;
            _outputLength = outputLength;
            _readBuffer = new byte[Math.Max(inputLength, 1)];
            _readPin = GCHandle.Alloc(_readBuffer, GCHandleType.Pinned);
            _readOverlapped = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>());
            _writeBuffer = new byte[Math.Max(outputLength, 1)];
            _writePin = GCHandle.Alloc(_writeBuffer, GCHandleType.Pinned);
            _writeOverlapped = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>());
        }

        /// <summary>Opens the collection. Pushed families need only read
        /// access, which leaves write access free for the vendor's software.
        /// Polled families write their requests.</summary>
        internal static AnalogKeyboardHidChannel Open(AnalogKeyboardCandidate candidate, bool writable)
        {
            uint access = SonyHeadsetHid.GENERIC_READ | (writable ? SonyHeadsetHid.GENERIC_WRITE : 0);
            var handle = SonyHeadsetHid.CreateFile(candidate.Path, access,
                SonyHeadsetHid.FILE_SHARE_READ | SonyHeadsetHid.FILE_SHARE_WRITE,
                IntPtr.Zero, SonyHeadsetHid.OPEN_EXISTING, SonyHeadsetHid.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (handle.IsInvalid) { handle.Dispose(); return null; }
            return new AnalogKeyboardHidChannel(handle, candidate.InputReportLength, candidate.OutputReportLength);
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
                if (SonyHeadsetHid.ReadFile(_handle, _readPin.AddrOfPinnedObject(),
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
            if (!SonyHeadsetHid.GetOverlappedResult(_handle, _readOverlapped, out uint bytes, false))
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
                SonyHeadsetHid.GetOverlappedResult(_handle, _readOverlapped, out _, false);
            }
            AnalogKeyboardHidRuntime.HidD_FlushQueue(_handle);
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

        /// <summary>From any thread: stop every wait and cancel the I/O. The
        /// owning thread sees the flag within one wait slice, leaves its loop
        /// and calls <see cref="Close"/>. The read event is left to the
        /// kernel: signaling it by hand while the read is still pending would
        /// let the owner free a buffer the kernel still owns.</summary>
        internal void Abort()
        {
            _aborted = true;
            try { if (!_handle.IsInvalid) SonyHeadsetHid.CancelIoEx(_handle, IntPtr.Zero); } catch { }
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
                    SonyHeadsetHid.CancelIoEx(_handle, _readOverlapped);
                    SonyHeadsetHid.GetOverlappedResult(_handle, _readOverlapped, out _, true);
                    _readPending = false;
                }
            }
            catch { }
            try { _handle.Dispose(); } catch { }
            if (_readPin.IsAllocated) _readPin.Free();
            if (_writePin.IsAllocated) _writePin.Free();
            Marshal.FreeHGlobal(_readOverlapped);
            Marshal.FreeHGlobal(_writeOverlapped);
            _readEvent.Dispose();
            _writeEvent.Dispose();
        }

        // IntPtr buffer and OVERLAPPED, never marshaled arrays: the kernel
        // reads the buffer after the call returns ERROR_IO_PENDING
        // (RawHidOutput's declaration, for the same reason).
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteFile(SafeFileHandle handle, IntPtr buffer, uint bytesToWrite,
            IntPtr bytesWritten, IntPtr overlapped);
    }
}
