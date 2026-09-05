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
        // layout with natural alignment is what the C compiler produces, so
        // the marshaler's offsets match the native ones on every field.
        [StructLayout(LayoutKind.Sequential)]
        private struct DeviceInfo
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

        /// <summary>The device paths of every present HID interface with this
        /// vendor id (0 for any) and this usage page (0 for any). SDL
        /// initializes its HID layer on demand, so this needs no prior init.
        /// Returns an empty list on any failure.</summary>
        public static List<string> Paths(ushort vendorId, ushort usagePage)
        {
            var result = new List<string>();
            IntPtr head = IntPtr.Zero;
            try
            {
                head = SDL_hid_enumerate(vendorId, 0);
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
            }
            catch
            {
                result.Clear();
            }
            finally
            {
                if (head != IntPtr.Zero)
                {
                    try { SDL_hid_free_enumeration(head); } catch { }
                }
            }
            return result;
        }
    }
}
