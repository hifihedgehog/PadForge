using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PadForge.Engine
{
    /// <summary>
    /// SDL's raw HID enumeration, for the one question the joystick list
    /// cannot answer: which HID interfaces of a vendor are PRESENT, whether
    /// or not any SDL driver claimed them (discussion #395). A Flydigi pad's
    /// vendor interface that SDL's Flydigi driver failed to claim at arrival
    /// is invisible in the joystick list, and its path is the only identity
    /// that ties it to the enhanced joystick SDL would create for it, since
    /// SDL names a HIDAPI joystick by that same path.
    /// </summary>
    public static class SdlHidEnumeration
    {
        // Mirrors SDL_hid_device_info in include/SDL3/SDL_hidapi.h. Sequential
        // layout with natural alignment is what the C compiler produces with
        // SDL's eight-byte packing on x64: 80 bytes, next at 72.
        [StructLayout(LayoutKind.Sequential)]
        internal struct DeviceInfo
        {
            public IntPtr path;                 // char*
            public ushort vendor_id;
            public ushort product_id;
            public IntPtr serial_number;        // wchar_t*
            public ushort release_number;
            public IntPtr manufacturer_string;  // wchar_t*
            public IntPtr product_string;       // wchar_t*
            public ushort usage_page;
            public ushort usage;
            public int interface_number;
            public int interface_class;
            public int interface_subclass;
            public int interface_protocol;
            public int bus_type;                // SDL_hid_bus_type
            public IntPtr next;
        }

        [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_hid_enumerate")]
        private static extern IntPtr SDL_hid_enumerate(ushort vendor_id, ushort product_id);

        [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_hid_free_enumeration")]
        private static extern void SDL_hid_free_enumeration(IntPtr devs);

        [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_hid_device_change_count")]
        private static extern uint SDL_hid_device_change_count();

        /// <summary>The managed mirror's size and the offsets of the fields
        /// the walk depends on, for the layout test.</summary>
        internal static (int size, int usagePage, int next) LayoutProbe()
            => (Marshal.SizeOf<DeviceInfo>(),
                (int)Marshal.OffsetOf<DeviceInfo>(nameof(DeviceInfo.usage_page)),
                (int)Marshal.OffsetOf<DeviceInfo>(nameof(DeviceInfo.next)));

        /// <summary>SDL's counter of HID device arrivals and removals, kept
        /// from Windows device notifications, so a caller can skip an
        /// expensive enumeration when nothing changed. Null when SDL could
        /// not be reached.</summary>
        public static uint? DeviceChangeCount()
        {
            try { return SDL_hid_device_change_count(); }
            catch { return null; }
        }

        [DllImport("SDL3", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SDL_hid_init")]
        private static extern int SDL_hid_init();

        private static bool _hidReady;

        /// <summary>The device paths of every present HID interface with this
        /// vendor id (0 for any) and this usage page (0 for any). SDL's HID
        /// layer is initialized once here and held (SDL refcounts it, and the
        /// joystick layer holds its own count), so a NULL enumeration result
        /// afterward is "no devices" and never a failed init. Returns null
        /// when SDL could not be reached or its init failed, which the caller
        /// must not read as "no devices". An empty list is a successful
        /// enumeration that found none.</summary>
        public static List<string> Paths(ushort vendorId, ushort usagePage)
        {
            var result = new List<string>();
            IntPtr head = IntPtr.Zero;
            try
            {
                if (!_hidReady)
                {
                    if (SDL_hid_init() < 0) return null;
                    _hidReady = true;
                }
                head = SDL_hid_enumerate(vendorId, 0);
                if (head == IntPtr.Zero) return result;
                for (IntPtr p = head; p != IntPtr.Zero;)
                {
                    var info = Marshal.PtrToStructure<DeviceInfo>(p);
                    if ((usagePage == 0 || info.usage_page == usagePage) && info.path != IntPtr.Zero)
                    {
                        string path = Marshal.PtrToStringUTF8(info.path);
                        if (!string.IsNullOrEmpty(path)) result.Add(path);
                    }
                    p = info.next;
                }
                return result;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (head != IntPtr.Zero)
                {
                    try { SDL_hid_free_enumeration(head); } catch { }
                }
            }
        }

    }
}
