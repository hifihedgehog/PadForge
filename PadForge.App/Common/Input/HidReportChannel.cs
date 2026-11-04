using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace PadForge.Common.Input
{
    /// <summary>One open HID collection as a direct peripheral backend
    /// speaks to it (#494): feature reports both ways, and output and input
    /// reports where the handle carries them. The production channel is
    /// <see cref="HidReportChannel"/>. Tests answer from a script. One
    /// thread uses a channel at a time, the worker that owns its family.</summary>
    internal interface IHidReportChannel : IDisposable
    {
        string Path { get; }

        /// <summary>True when the collection could be opened with no access
        /// right only, as a mouse or keyboard collection is. Feature reports
        /// still pass on such a handle and nothing else does.</summary>
        bool FeatureOnly { get; }

        /// <summary>The byte length of the collection's longest report of
        /// each type, the report ID byte included, or 0 for a type it has
        /// none of.</summary>
        int InputReportLength { get; }
        int OutputReportLength { get; }
        int FeatureReportLength { get; }

        /// <summary>Sends a feature report. Byte 0 is the report ID, 0 for a
        /// collection without one. A report shorter than the collection's
        /// feature length goes out padded with zeros.</summary>
        bool SetFeature(byte[] report);

        /// <summary>Reads the feature report with this ID into
        /// <paramref name="buffer"/>, whose byte 0 comes back as the ID.</summary>
        bool GetFeature(byte reportId, byte[] buffer);

        /// <summary>Writes an output report, padded like a feature report,
        /// and waits at most <paramref name="timeoutMs"/> for it. False on a
        /// feature-only handle, which never carries one.</summary>
        bool WriteOutput(byte[] report, int timeoutMs = HidReportChannel.WriteTimeoutMs);

        /// <summary>Waits at most <paramref name="timeoutMs"/> for one input
        /// report and copies it into <paramref name="buffer"/>, byte 0 the
        /// report ID as Windows delivers it, 0 for a collection without one.
        /// The number of bytes copied, 0 when none came in time, or -1 when
        /// the read failed or the handle is feature-only. A SteelSeries
        /// mouse on its wireless dongle answers each write with a report
        /// that has to be read back before the next one (OpenRGB
        /// SteelSeriesAeroxWirelessController.cpp:254-268).</summary>
        int ReadInput(byte[] buffer, int timeoutMs);

        /// <summary>The device's manufacturer, product and serial number
        /// strings as the HID class driver hands them
        /// (HidD_GetManufacturerString, HidD_GetProductString,
        /// HidD_GetSerialNumberString): reads the driver answers from the
        /// device's string descriptors, which pass on a handle with no access
        /// right as well, where hidapi reads them (hidapi windows/hid.c:786-801,
        /// on the handle opened at line 878). Empty for a string the device
        /// does not have, null when the read failed. A channel that cannot
        /// read them answers null.</summary>
        string ManufacturerString => null;

        string ProductString => null;

        string SerialNumber => null;
    }

    /// <summary>
    /// The channel behind every direct family that speaks HID (#494).
    ///
    /// <para>The open is hidapi's ladder (hidapi windows/hid.c:357-372 and
    /// 1009-1019). First read and write access, shared, which a vendor
    /// collection and a LampArray collection grant. A mouse or keyboard
    /// collection refuses that, since Windows holds those itself, and then
    /// the open is tried again with no access right at all: "feature
    /// reports can still be sent and received" on such a handle (hid.c:1014-1018),
    /// and a Razer device is driven through its mouse collection that way.
    /// hidapi retries after any failure. This retries after access denied
    /// and after a sharing violation only, the two refusals Windows gives
    /// for a collection that is there, so a collection that left costs one
    /// open and not two.</para>
    ///
    /// <para>A report shorter than the collection's length is padded with
    /// zeros before it goes out. Windows wants the length of the
    /// collection's longest report for an output report (hid.c:1096-1119),
    /// and fails a feature report any shorter with ERROR_INVALID_PARAMETER
    /// (hid.c:1279-1300). HidD_SetFeature and HidD_GetFeature take no
    /// timeout, so a stalled device blocks the caller for as long as the
    /// bus driver waits. A worker can afford that on its own thread and
    /// the crash path cannot, so its writes go through
    /// <see cref="RunBounded"/>.</para>
    /// </summary>
    internal sealed class HidReportChannel : IHidReportChannel
    {
        internal const int ErrorAccessDenied = 5;
        internal const int ErrorSharingViolation = 32;

        /// <summary>How long an output report may take, hidapi's default
        /// write timeout (hid.c:234).</summary>
        internal const int WriteTimeoutMs = 1000;

        private readonly SafeFileHandle _handle;
        private byte[] _featureScratch;
        private byte[] _outputScratch;
        private byte[] _readScratch;
        private ManualResetEvent _ioEvent;
        private IntPtr _overlapped;
        private int _disposed;

        public string Path { get; }
        public bool FeatureOnly { get; }
        public int InputReportLength { get; }
        public int OutputReportLength { get; }
        public int FeatureReportLength { get; }

        private HidReportChannel(string path, SafeFileHandle handle, bool featureOnly, SonyHeadsetHid.HIDP_CAPS caps)
        {
            Path = path;
            _handle = handle;
            FeatureOnly = featureOnly;
            InputReportLength = caps.InputReportByteLength;
            OutputReportLength = caps.OutputReportByteLength;
            FeatureReportLength = caps.FeatureReportByteLength;
        }

        /// <summary>Opens a collection by the ladder, or returns null when
        /// neither rung opens it or its descriptor cannot be read.</summary>
        public static HidReportChannel Open(string path) => Open(path, featureOnly: false);

        /// <summary>Opens a collection with no access right from the start,
        /// for a scan that must ask Windows for nothing a second writer
        /// could notice. Feature reports pass and nothing else does.</summary>
        public static HidReportChannel OpenFeatureOnly(string path) => Open(path, featureOnly: true);

        private static HidReportChannel Open(string path, bool featureOnly)
        {
            if (string.IsNullOrEmpty(path)) return null;
            // The test host opens no device (RealHidAccess).
            if (RealHidAccess.Refused) return null;
            SafeFileHandle handle;
            if (featureOnly) handle = OpenHandle(path, 0).Handle;
            else handle = OpenLadder(access => OpenHandle(path, access), out featureOnly);
            if (handle == null) return null;

            IntPtr preparsed = IntPtr.Zero;
            try
            {
                if (SonyHeadsetHid.HidD_GetPreparsedData(handle, out preparsed)
                    && SonyHeadsetHid.HidP_GetCaps(preparsed, out var caps) == SonyHeadsetHid.HIDP_STATUS_SUCCESS)
                    return new HidReportChannel(path, handle, featureOnly, caps);
            }
            catch { }
            finally
            {
                if (preparsed != IntPtr.Zero) SonyHeadsetHid.HidD_FreePreparsedData(preparsed);
            }
            handle.Dispose();
            return null;
        }

        /// <summary>Test seam: a channel over a handle the caller opened, at
        /// the report lengths it names, so the overlapped write and read run
        /// against a pipe with no device behind them. The channel owns the
        /// handle from here on.</summary>
        internal static HidReportChannel ForHandle(string path, SafeFileHandle handle, bool featureOnly,
            ushort inputLength, ushort outputLength, ushort featureLength)
            => new(path, handle, featureOnly, new SonyHeadsetHid.HIDP_CAPS
            {
                InputReportByteLength = inputLength,
                OutputReportByteLength = outputLength,
                FeatureReportByteLength = featureLength,
            });

        /// <summary>One open at an access mask: the handle, or null with the
        /// error Windows gave.</summary>
        private static (SafeFileHandle Handle, int Error) OpenHandle(string path, uint access)
        {
            var handle = SonyHeadsetHid.CreateFile(path, access,
                SonyHeadsetHid.FILE_SHARE_READ | SonyHeadsetHid.FILE_SHARE_WRITE,
                IntPtr.Zero, SonyHeadsetHid.OPEN_EXISTING, SonyHeadsetHid.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (!handle.IsInvalid) return (handle, 0);
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            return (null, error);
        }

        /// <summary>The ladder itself, apart from the open it climbs with, so
        /// a test walks it with scripted errors. <paramref name="open"/>
        /// takes an access mask and answers a handle, or null and the error.
        /// <paramref name="featureOnly"/> is set when the second rung opened
        /// the collection.</summary>
        internal static THandle OpenLadder<THandle>(Func<uint, (THandle Handle, int Error)> open, out bool featureOnly)
            where THandle : class
        {
            featureOnly = false;
            var (handle, error) = open(SonyHeadsetHid.GENERIC_READ | SonyHeadsetHid.GENERIC_WRITE);
            if (handle != null) return handle;
            if (error != ErrorAccessDenied && error != ErrorSharingViolation) return null;
            (handle, _) = open(0);
            featureOnly = handle != null;
            return handle;
        }

        /// <summary>A report at the length Windows wants: itself when it is
        /// long enough, else a copy padded with zeros in
        /// <paramref name="scratch"/>, which is made on first use and reused
        /// after. A longer report is left alone, as hidapi leaves it.</summary>
        internal static byte[] Pad(byte[] report, int length, ref byte[] scratch)
        {
            if (report.Length >= length) return report;
            if (scratch == null || scratch.Length != length) scratch = new byte[length];
            Array.Copy(report, scratch, report.Length);
            Array.Clear(scratch, report.Length, length - report.Length);
            return scratch;
        }

        public bool SetFeature(byte[] report)
        {
            if (report == null || report.Length == 0 || FeatureReportLength == 0) return false;
            if (Volatile.Read(ref _disposed) != 0) return false;
            byte[] sized = Pad(report, FeatureReportLength, ref _featureScratch);
            try { return SonyHeadsetHid.HidD_SetFeature(_handle, sized, (uint)sized.Length); }
            catch { return false; }
        }

        public bool GetFeature(byte reportId, byte[] buffer)
        {
            if (buffer == null || buffer.Length == 0 || FeatureReportLength == 0) return false;
            if (Volatile.Read(ref _disposed) != 0) return false;
            // The read is made at the collection's own length, whatever the
            // caller's buffer holds, and the ID goes in ahead of it.
            var scratch = _readScratch;
            if (scratch == null || scratch.Length < FeatureReportLength)
                _readScratch = scratch = new byte[Math.Max(FeatureReportLength, InputReportLength)];
            Array.Clear(scratch, 0, FeatureReportLength);
            scratch[0] = reportId;
            try
            {
                if (!SonyHeadsetHid.HidD_GetFeature(_handle, scratch, (uint)FeatureReportLength)) return false;
            }
            catch { return false; }
            int copied = Math.Min(FeatureReportLength, buffer.Length);
            Array.Copy(scratch, buffer, copied);
            if (buffer.Length > copied) Array.Clear(buffer, copied, buffer.Length - copied);
            return true;
        }

        public bool WriteOutput(byte[] report, int timeoutMs = WriteTimeoutMs)
        {
            if (FeatureOnly || report == null || report.Length == 0 || OutputReportLength == 0) return false;
            if (Volatile.Read(ref _disposed) != 0) return false;
            byte[] sized = Pad(report, OutputReportLength, ref _outputScratch);
            return Transfer(sized, sized.Length, timeoutMs, write: true) > 0;
        }

        public int ReadInput(byte[] buffer, int timeoutMs)
        {
            if (FeatureOnly || buffer == null || buffer.Length == 0 || InputReportLength == 0) return -1;
            if (Volatile.Read(ref _disposed) != 0) return -1;
            var scratch = _readScratch;
            if (scratch == null || scratch.Length < InputReportLength)
                _readScratch = scratch = new byte[Math.Max(FeatureReportLength, InputReportLength)];
            int read = Transfer(scratch, InputReportLength, timeoutMs, write: false);
            if (read <= 0) return read;
            int copied = Math.Min(read, buffer.Length);
            Array.Copy(scratch, buffer, copied);
            return copied;
        }

        /// <summary>One overlapped write or read with a bounded wait. The
        /// bytes moved, 0 for a read nothing answered in time, or -1 on a
        /// failure, a write that timed out included. The buffer stays pinned
        /// and the OVERLAPPED stays allocated until the request has
        /// completed or its cancel has, so the kernel never writes into
        /// memory that moved or was freed (RawHidOutput.WriteOnHandle's
        /// drain).</summary>
        private int Transfer(byte[] buffer, int length, int timeoutMs, bool write)
        {
            _ioEvent ??= new ManualResetEvent(false);
            if (_overlapped == IntPtr.Zero)
                _overlapped = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>());
            var pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                _ioEvent.Reset();
                Marshal.StructureToPtr(new NativeOverlapped
                {
                    EventHandle = _ioEvent.SafeWaitHandle.DangerousGetHandle(),
                }, _overlapped, false);
                uint moved;
                bool done = write
                    ? WriteFile(_handle, pin.AddrOfPinnedObject(), (uint)length, out moved, _overlapped)
                    : SonyHeadsetHid.ReadFile(_handle, pin.AddrOfPinnedObject(), (uint)length, out moved, _overlapped);
                if (done) return (int)moved;
                if (Marshal.GetLastWin32Error() != SonyHeadsetHid.ERROR_IO_PENDING) return -1;
                if (!_ioEvent.WaitOne(Math.Max(0, timeoutMs)))
                {
                    // A cancel is only asked for: wait for it to land before
                    // the pin and the OVERLAPPED are let go. A request that
                    // completed in the meantime is taken as it came.
                    SonyHeadsetHid.CancelIoEx(_handle, _overlapped);
                    if (SonyHeadsetHid.GetOverlappedResult(_handle, _overlapped, out moved, true)) return (int)moved;
                    return write ? -1 : 0;
                }
                return SonyHeadsetHid.GetOverlappedResult(_handle, _overlapped, out moved, false) ? (int)moved : -1;
            }
            catch
            {
                return -1;
            }
            finally
            {
                pin.Free();
            }
        }

        /// <summary>Runs one blocking HID call on a helper thread and waits
        /// at most <paramref name="timeoutMs"/> for it, for the crash path,
        /// which must end on time whatever a device does. True when the call
        /// finished in time and returned true. A call that outlasts the wait
        /// is left to finish on its background thread, which holds no
        /// process open.</summary>
        internal static bool RunBounded(Func<bool> call, int timeoutMs)
        {
            if (call == null) return false;
            var outcome = new BoundedCall();
            var helper = new Thread(() =>
            {
                try { outcome.Result = call(); }
                catch { }
                finally { outcome.Done.Set(); }
            })
            { IsBackground = true, Name = "PeripheralHidOneShot" };
            try { helper.Start(); }
            catch { return false; }
            // Never disposed: the helper may still be about to set it, and a
            // ManualResetEventSlim whose WaitHandle was never read holds no
            // kernel handle.
            return outcome.Done.Wait(Math.Max(0, timeoutMs)) && outcome.Result;
        }

        private sealed class BoundedCall
        {
            public readonly ManualResetEventSlim Done = new(false);
            public volatile bool Result;
        }

        /// <summary>One feature report through a handle opened for it alone
        /// and closed again, open included in the bound. The crash path's
        /// hand-back, which cannot wait on a worker's channel.</summary>
        public static bool SetFeatureOnce(string path, byte[] report, int timeoutMs)
            => RunBounded(() =>
            {
                using var channel = Open(path);
                return channel != null && channel.SetFeature(report);
            }, timeoutMs);

        /// <summary>One output report the same way.</summary>
        public static bool WriteOutputOnce(string path, byte[] report, int timeoutMs)
            => RunBounded(() =>
            {
                using var channel = Open(path);
                return channel != null && channel.WriteOutput(report, timeoutMs);
            }, timeoutMs);

        public string ManufacturerString => ReadString(HidD_GetManufacturerString);

        public string ProductString => ReadString(SonyHeadsetHid.HidD_GetProductString);

        public string SerialNumber => ReadString(HidD_GetSerialNumberString);

        private delegate bool StringRead(SafeFileHandle handle, byte[] buffer, uint length);

        /// <summary>One string read, up to its first NUL, in a buffer of 256
        /// wide characters and the NUL, hidapi's MAX_STRING_WCHARS
        /// (hid.c:80 and 743-786).</summary>
        private string ReadString(StringRead read)
        {
            if (Volatile.Read(ref _disposed) != 0) return null;
            var buffer = new byte[(MaxStringChars + 1) * 2];
            try
            {
                if (!read(_handle, buffer, (uint)(MaxStringChars * 2))) return null;
            }
            catch
            {
                return null;
            }
            string text = System.Text.Encoding.Unicode.GetString(buffer);
            int nul = text.IndexOf('\0');
            return nul >= 0 ? text.Substring(0, nul) : text;
        }

        internal const int MaxStringChars = 256;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { _handle.Dispose(); } catch { }
            if (_overlapped != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_overlapped);
                _overlapped = IntPtr.Zero;
            }
            _ioEvent?.Dispose();
            _ioEvent = null;
        }

        // SonyHeadsetHid declares the read this pairs with. The buffer and
        // the OVERLAPPED cross as raw pointers for the same reason: the
        // kernel owns both until the write completes, after the call
        // returned.
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteFile(SafeFileHandle handle, IntPtr buffer,
            uint bytesToWrite, out uint bytesWritten, IntPtr overlapped);

        [DllImport("hid.dll")]
        private static extern bool HidD_GetManufacturerString(SafeFileHandle handle, byte[] buffer, uint bufferLength);

        [DllImport("hid.dll")]
        private static extern bool HidD_GetSerialNumberString(SafeFileHandle handle, byte[] buffer, uint bufferLength);
    }
}
