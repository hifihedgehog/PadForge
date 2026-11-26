using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PadForge.Tools.OutputFamilyProbe
{
    /// <summary>One present interface of the HID class: its path and the
    /// instance ID of the devnode behind it.</summary>
    internal sealed record HidInterface(string Path, string InstanceId);

    /// <summary>SetupAPI over the HID interface class (HidD_GetHidGuid), as
    /// HIDMaestro's own enumerator lists HID devices
    /// (sdk/HIDMaestro.Core/Internal/HidDeviceEnumerator.cs).</summary>
    internal static class HidInterfaces
    {
        internal static List<HidInterface> Present()
        {
            var list = new List<HidInterface>();
            Native.HidD_GetHidGuid(out Guid hid);
            IntPtr set = Native.SetupDiGetClassDevsW(ref hid, IntPtr.Zero, IntPtr.Zero,
                Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);
            if (set == new IntPtr(-1)) return list;
            try
            {
                var data = new Native.SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<Native.SP_DEVICE_INTERFACE_DATA>() };
                for (int i = 0; Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hid, i, ref data); i++)
                {
                    var info = new Native.SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<Native.SP_DEVINFO_DATA>() };
                    Native.SetupDiGetDeviceInterfaceDetailW(set, ref data, IntPtr.Zero, 0, out int need, ref info);
                    if (need <= 0) continue;
                    IntPtr detail = Marshal.AllocHGlobal(need);
                    try
                    {
                        // SP_DEVICE_INTERFACE_DETAIL_DATA_W: cbSize is 8 on x64,
                        // the path follows the DWORD.
                        Marshal.WriteInt32(detail, 8);
                        if (!Native.SetupDiGetDeviceInterfaceDetailW(set, ref data, detail, need, out _, ref info)) continue;
                        string path = Marshal.PtrToStringUni(detail + 4);
                        var id = new char[512];
                        string instance = Native.SetupDiGetDeviceInstanceIdW(set, ref info, id, id.Length, out _)
                            ? new string(id, 0, Array.IndexOf(id, '\0') is int n && n >= 0 ? n : id.Length)
                            : string.Empty;
                        if (!string.IsNullOrEmpty(path)) list.Add(new HidInterface(path, instance));
                    }
                    finally { Marshal.FreeHGlobal(detail); }
                }
            }
            finally { Native.SetupDiDestroyDeviceInfoList(set); }
            return list;
        }

        /// <summary>The interfaces present now that were not present in
        /// <paramref name="before"/>, by path.</summary>
        internal static List<HidInterface> NewSince(IReadOnlyCollection<HidInterface> before)
        {
            var known = new HashSet<string>(before.Select(b => b.Path), StringComparer.OrdinalIgnoreCase);
            return Present().Where(p => !known.Contains(p.Path)).ToList();
        }
    }

    /// <summary>An open HID interface with its parser data, read with a
    /// bounded overlapped read.</summary>
    internal sealed unsafe class OpenHid : IDisposable
    {
        public HidInterface Interface { get; }
        public ushort VendorId { get; }
        public ushort ProductId { get; }
        public ushort UsagePage { get; }
        public ushort Usage { get; }
        public int InputReportLength { get; }
        public int FeatureReportLength { get; }
        public IntPtr Preparsed { get; private set; }

        private readonly SafeFileHandle _file;
        private readonly IntPtr _event;
        private readonly byte* _buffer;
        private readonly Native.OVERLAPPED* _overlapped;
        private bool _pending;

        private OpenHid(HidInterface itf, SafeFileHandle file, Native.HIDD_ATTRIBUTES attr, IntPtr preparsed, Native.HIDP_CAPS caps)
        {
            Interface = itf;
            _file = file;
            VendorId = attr.VendorID;
            ProductId = attr.ProductID;
            Preparsed = preparsed;
            UsagePage = caps.UsagePage;
            Usage = caps.Usage;
            InputReportLength = caps.InputReportByteLength;
            FeatureReportLength = caps.FeatureReportByteLength;
            _event = Native.CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);
            _buffer = (byte*)NativeMemory.AllocZeroed((nuint)Math.Max(1, InputReportLength));
            _overlapped = (Native.OVERLAPPED*)NativeMemory.AllocZeroed((nuint)sizeof(Native.OVERLAPPED));
        }

        /// <summary>Opens the interface for reading, or returns null with the
        /// reason: Windows opens keyboard and mouse collections for itself
        /// alone, and those refuse.</summary>
        internal static OpenHid TryOpen(HidInterface itf, out string why)
        {
            why = null;
            var file = Native.CreateFileW(itf.Path, Native.GENERIC_READ | Native.GENERIC_WRITE,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING,
                Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (file.IsInvalid)
            {
                // A read-only open, the access SDL's hidapi falls back to.
                file = Native.CreateFileW(itf.Path, Native.GENERIC_READ,
                    Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING,
                    Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            }
            if (file.IsInvalid)
            {
                why = $"open refused, error {Marshal.GetLastWin32Error()}";
                return null;
            }
            var attr = new Native.HIDD_ATTRIBUTES { Size = Marshal.SizeOf<Native.HIDD_ATTRIBUTES>() };
            if (!Native.HidD_GetAttributes(file, ref attr))
            {
                why = $"HidD_GetAttributes failed, error {Marshal.GetLastWin32Error()}";
                file.Dispose();
                return null;
            }
            if (!Native.HidD_GetPreparsedData(file, out IntPtr pp) || pp == IntPtr.Zero)
            {
                why = "no preparsed data";
                file.Dispose();
                return null;
            }
            var caps = new Native.HIDP_CAPS { Reserved = new ushort[17] };
            if (Native.HidP_GetCaps(pp, ref caps) != Native.HIDP_STATUS_SUCCESS || caps.InputReportByteLength == 0)
            {
                why = "the collection declares no input report";
                Native.HidD_FreePreparsedData(pp);
                file.Dispose();
                return null;
            }
            return new OpenHid(itf, file, attr, pp, caps);
        }

        /// <summary>Reads one feature report, as a host reads one when it
        /// opens a pad. True when the device answered.</summary>
        internal bool GetFeature(byte reportId)
        {
            if (FeatureReportLength == 0) return false;
            var buf = new byte[FeatureReportLength];
            buf[0] = reportId;
            return Native.HidD_GetFeature(_file, buf, buf.Length);
        }

        /// <summary>Starts a read if none is pending.</summary>
        private bool Arm()
        {
            if (_pending) return true;
            Native.ResetEvent(_event);
            *_overlapped = default;
            _overlapped->hEvent = _event;
            if (Native.ReadFile(_file, _buffer, InputReportLength, IntPtr.Zero, _overlapped))
            {
                _pending = true;   // completed at once; the event is set
                return true;
            }
            if (Marshal.GetLastWin32Error() != Native.ERROR_IO_PENDING) return false;
            _pending = true;
            return true;
        }

        /// <summary>One input report, or null when none arrived within
        /// <paramref name="waitMs"/>. A read that times out stays pending
        /// and is picked up by the next call.</summary>
        internal byte[] Read(uint waitMs)
        {
            if (!Arm()) return null;
            if (Native.WaitForSingleObject(_event, waitMs) != Native.WAIT_OBJECT_0) return null;
            _pending = false;
            if (!Native.GetOverlappedResult(_file, _overlapped, out int n, false) || n <= 0) return null;
            var report = new byte[n];
            new ReadOnlySpan<byte>(_buffer, n).CopyTo(report);
            return report;
        }

        public void Dispose()
        {
            if (_pending)
            {
                // The buffer stays until the cancel has completed.
                Native.CancelIoEx(_file, _overlapped);
                Native.GetOverlappedResult(_file, _overlapped, out _, true);
                _pending = false;
            }
            _file.Dispose();
            if (Preparsed != IntPtr.Zero) { Native.HidD_FreePreparsedData(Preparsed); Preparsed = IntPtr.Zero; }
            NativeMemory.Free(_buffer);
            NativeMemory.Free(_overlapped);
            Native.CloseHandle(_event);
        }
    }
}
