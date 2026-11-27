using System.Diagnostics;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace PadForge.Tools.OutputFamilyProbe
{
    /// <summary>The host start a Switch 2 Pro Controller waits for. The
    /// composite persona, as the pad itself on USB, sends no input report
    /// until a host sends command 03/0D on interface 1's bulk pair
    /// (switch2-pro-composite.json notes and its protocol rule
    /// "03 .. .. 0D", which sets reportsOn). SDL sends it last in its
    /// Switch 2 start (SDL_hidapi_switch2.c:547-550, "Start output"), and
    /// HIDMaestro's own live check sends the same 16 bytes through WinUSB
    /// (test/probes/switch2_composite_check/Live.cs:480) and reads one reply
    /// as SDL's RecvBulkData does. The interface is found from the HID
    /// interface the probe opened: its USB function, the composite device
    /// above it, and that device's interface 1, whose WinUSB device interface
    /// GUID Windows keeps in the function's Device Parameters (Live.cs
    /// Discover and LiveBinding).</summary>
    internal sealed class BulkStart : IDisposable
    {
        internal static bool Needs(Family f)
            => string.Equals(f.ProfileId, "switch2-pro-controller-composite", StringComparison.OrdinalIgnoreCase);

        /// <summary>SDL_hidapi_switch2.c:547-550: command 03, subcommand 0D,
        /// eight data bytes.</summary>
        internal static readonly byte[] StartCommand =
            { 0x03, 0x91, 0x00, 0x0D, 0x00, 0x08, 0x00, 0x00, 0x01, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };

        /// <summary>The reply the persona gives (Live.cs:481): the command
        /// echoed with status 01 and 01 00 00 00.</summary>
        internal static readonly byte[] StartReply =
            { 0x03, 0x01, 0x00, 0x0D, 0x00, 0xF8, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00 };

        private const byte PipeOut = 0x02, PipeIn = 0x82;

        private readonly SafeFileHandle _file;
        private IntPtr _usb;

        private BulkStart(SafeFileHandle file, IntPtr usb)
        {
            _file = file;
            _usb = usb;
        }

        /// <summary>Opens interface 1 of the USB device the HID interface
        /// belongs to, or returns null with the reason.</summary>
        internal static BulkStart Open(OpenHid hid, out string why)
        {
            why = null;
            string vendor = VendorInterfaceOf(hid.Interface.InstanceId);
            if (vendor == null) { why = $"no interface 1 beside {hid.Interface.InstanceId}"; return null; }
            string guidText;
            using (var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{vendor}\Device Parameters"))
            {
                guidText = k?.GetValue("DeviceInterfaceGUID") as string
                    ?? (k?.GetValue("DeviceInterfaceGUIDs") as string[])?.FirstOrDefault();
            }
            if (!Guid.TryParse(guidText, out Guid guid)) { why = $"{vendor} has no DeviceInterfaceGUID"; return null; }
            string path = InterfacePath(guid, vendor);
            if (path == null) { why = $"{vendor} has no device interface {guid:B}"; return null; }
            var file = Native.CreateFileW(path, Native.GENERIC_READ | Native.GENERIC_WRITE,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING,
                Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (file.IsInvalid) { why = $"open {path}: error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}"; return null; }
            if (!Native.WinUsb_Initialize(file, out IntPtr usb))
            {
                why = $"WinUsb_Initialize: error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}";
                file.Dispose();
                return null;
            }
            uint inMs = 250, outMs = 1000;
            Native.WinUsb_SetPipePolicy(usb, PipeIn, Native.PIPE_TRANSFER_TIMEOUT, 4, ref inMs);
            Native.WinUsb_SetPipePolicy(usb, PipeOut, Native.PIPE_TRANSFER_TIMEOUT, 4, ref outMs);
            return new BulkStart(file, usb);
        }

        /// <summary>Sends the start and reads its one reply. Null when the
        /// write failed. No second read follows a read that came back empty:
        /// usbip-win2 completes a timed-out read with the last reply's bytes
        /// (the comment above Live.cs part B3).</summary>
        internal byte[] Start()
        {
            if (!Native.WinUsb_WritePipe(_usb, PipeOut, StartCommand, (uint)StartCommand.Length, out uint sent, IntPtr.Zero)
                || sent != StartCommand.Length)
                return null;
            var reply = new byte[64];
            return Native.WinUsb_ReadPipe(_usb, PipeIn, reply, (uint)reply.Length, out uint n, IntPtr.Zero)
                ? reply.AsSpan(0, (int)n).ToArray()
                : Array.Empty<byte>();
        }

        /// <summary>The device's interface 1: the function above the HID
        /// collection is interface 0, its parent the composite device, and
        /// the sibling whose id carries MI_01 interface 1.</summary>
        private static string VendorInterfaceOf(string hidInstanceId)
        {
            if (Native.CM_Locate_DevNodeW(out uint hid, hidInstanceId, 0) != 0) return null;
            if (Native.CM_Get_Parent(out uint function, hid, 0) != 0) return null;
            if (Native.CM_Get_Parent(out uint device, function, 0) != 0) return null;
            if (Native.CM_Get_Child(out uint child, device, 0) != 0) return null;
            do
            {
                var buf = new char[512];
                if (Native.CM_Get_Device_IDW(child, buf, buf.Length, 0) == 0)
                {
                    string id = new string(buf, 0, Math.Max(0, Array.IndexOf(buf, '\0')));
                    if (id.Contains("&MI_01", StringComparison.OrdinalIgnoreCase)) return id;
                }
            }
            while (Native.CM_Get_Sibling(out child, child, 0) == 0);
            return null;
        }

        private static string InterfacePath(Guid guid, string instanceId)
        {
            if (Native.CM_Get_Device_Interface_List_SizeW(out int len, ref guid, instanceId, 0) != 0 || len <= 1) return null;
            var buf = new char[len];
            if (Native.CM_Get_Device_Interface_ListW(ref guid, instanceId, buf, len, 0) != 0) return null;
            string first = new string(buf).Split('\0')[0];
            return first.Length > 0 ? first : null;
        }

        /// <summary>The HID interfaces' reports in a window before the start:
        /// none, for a pad that waits on it. The same-window control for the
        /// reports that follow it.</summary>
        internal static int ReportsWithin(List<OpenHid> open, int ms)
        {
            int count = 0;
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                bool any = false;
                foreach (var h in open)
                    if (h.Read(10) != null) { count++; any = true; }
                if (!any) Thread.Sleep(1);
            }
            return count;
        }

        public void Dispose()
        {
            if (_usb != IntPtr.Zero) { Native.WinUsb_Free(_usb); _usb = IntPtr.Zero; }
            _file.Dispose();
        }
    }
}
