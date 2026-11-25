using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace PadForge.Engine.Common
{
    /// <summary>
    /// The relative axes of a game controller, read from its own HID reports
    /// through Raw Input (#600, "This axis moves relatively"): a trackball's
    /// X and Y, a spinner's Dial or Wheel, any value its report descriptor
    /// marks Relative. Each report's value is a movement, which is added to
    /// a running total per usage.
    /// <para>Why not SDL's axis: SDL reads a generic HID controller through
    /// DirectInput, which hands a value of the report scaled into the range
    /// SDL sets (SDL_dinputjoystick.c, EnumDevObjectsCallback sets DIPROP_RANGE
    /// to -32768..32767), held until the next report. Wine's DirectInput,
    /// which Microsoft's behavior is tested against, types every value of a
    /// HID controller as DIDFT_ABSAXIS (dlls/dinput/joystick_hid.c,
    /// set_axis_type) and scales the report's value (scale_axis_value). A
    /// relative usage then reads as its last movement, held: a steady turn
    /// sends the same number over and over and the axis does not change,
    /// and a movement that repeats is lost. The report itself carries every
    /// movement.</para>
    /// <para>Which SDL axis a usage is follows SDL's own order: DirectInput
    /// names each value by usage (X, Y, Z or Wheel, Rx, Ry, Rz, Slider or
    /// Dial, and the Simulation page's steering, accelerator, brake, rudder
    /// and throttle, Wine's object_usage_to_guid), SDL gives each an offset in
    /// DIJOYSTATE2 by that name and sorts the axes by offset
    /// (SortDevObjects).</para>
    /// <para>The pump thread of RawInputListener is the only writer. Totals
    /// are read on the polling thread with Interlocked.</para>
    /// </summary>
    public static class RelativeHidAxes
    {
        /// <summary>One value of a report.</summary>
        internal readonly record struct Value(ushort Page, ushort Usage, byte ReportId, ushort LinkCollection,
            int BitSize, int LogicalMin, bool Relative);

        /// <summary>A controller's values, the SDL axis each is, and the
        /// running totals of the relative ones.</summary>
        internal sealed class Layout
        {
            public string Path;
            public Value[] Values;
            public long[] Totals;
            /// <summary>The value of each SDL axis, in SDL's order, with every
            /// value SDL can name.</summary>
            public int[] Order;
            /// <summary>The same order without the relative values, for a
            /// DirectInput that does not keep them.</summary>
            public int[] OrderWithoutRelative;
            public IntPtr Preparsed;
        }

        // Each Raw Input handle's layout, or null for a handle that is no
        // game controller or has no relative value.
        private static readonly ConcurrentDictionary<IntPtr, Layout> s_byHandle = new();
        private static readonly ConcurrentDictionary<string, Layout> s_byPath = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The DIJOYSTATE2 slot SDL sorts a value into: 0 to 5 for
        /// X, Y, Z, Rx, Ry and Rz, 6 for a slider, and -1 for a value SDL
        /// does not name (its EnumDevObjectsCallback passes over an axis it
        /// cannot grok).</summary>
        internal static int Slot(ushort page, ushort usage)
        {
            if (page == 0x01)
            {
                switch (usage)
                {
                    case 0x30: return 0;              // X
                    case 0x31: return 1;              // Y
                    case 0x32: return 2;              // Z
                    case 0x38: return 2;              // Wheel, read as Z
                    case 0x33: return 3;              // Rx
                    case 0x34: return 4;              // Ry
                    case 0x35: return 5;              // Rz
                    case 0x36: return 6;              // Slider
                    case 0x37: return 6;              // Dial, read as a slider
                }
            }
            else if (page == 0x02)
            {
                switch (usage)
                {
                    case 0xC8: return 0;              // Steering, read as X
                    case 0xC4: return 1;              // Accelerator, read as Y
                    case 0xC5: return 5;              // Brake, read as Rz
                    case 0xBA: return 5;              // Rudder, read as Rz
                    case 0xBB: return 6;              // Throttle, read as a slider
                }
            }
            return -1;
        }

        /// <summary>SDL's axis order over a controller's values: each named
        /// value sorted by its DIJOYSTATE2 offset, sliders in the order the
        /// device lists them after the six axes, a second value for a taken
        /// axis slot passed over as SDL's offsets would collide. With
        /// <paramref name="withoutRelative"/> the relative values are left
        /// out.</summary>
        internal static int[] SdlOrder(IReadOnlyList<Value> values, bool withoutRelative)
        {
            var axes = new int[6];
            Array.Fill(axes, -1);
            var sliders = new List<int>();
            for (int i = 0; i < values.Count; i++)
            {
                if (withoutRelative && values[i].Relative) continue;
                int slot = Slot(values[i].Page, values[i].Usage);
                if (slot < 0) continue;
                if (slot == 6) sliders.Add(i);
                else if (axes[slot] < 0) axes[slot] = i;
            }
            var order = new List<int>();
            foreach (int a in axes) if (a >= 0) order.Add(a);
            order.AddRange(sliders);
            return order.ToArray();
        }

        /// <summary>A report's raw value read as the signed number it is:
        /// sign-extended from its bit size when its logical minimum is below
        /// zero (Wine's sign_extend).</summary>
        internal static long SignExtend(uint raw, int bitSize, int logicalMin)
        {
            if (logicalMin >= 0 || bitSize <= 0) return raw;
            if (bitSize >= 32) return unchecked((int)raw);
            uint sign = 1u << (bitSize - 1);
            uint mask = (1u << bitSize) - 1;
            raw &= mask;
            return (raw & sign) != 0 ? (long)raw - (1L << bitSize) : raw;
        }

        /// <summary>The running total of the SDL axis <paramref name="sdlAxis"/>
        /// of the controller at <paramref name="devicePath"/>, when that axis is
        /// a relative value of its reports. <paramref name="sdlAxisCount"/> is
        /// how many axes SDL lists, which says whether DirectInput kept the
        /// relative values. False for any other axis or device.</summary>
        public static bool TryGetTotal(string devicePath, int sdlAxis, int sdlAxisCount, out long total)
        {
            total = 0;
            if (string.IsNullOrEmpty(devicePath) || !s_byPath.TryGetValue(devicePath, out var layout)) return false;
            int v = ValueOfAxis(layout, sdlAxis, sdlAxisCount);
            if (v < 0 || !layout.Values[v].Relative) return false;
            total = Interlocked.Read(ref layout.Totals[v]);
            return true;
        }

        /// <summary>The value behind a PadForge axis of a controller. With as
        /// many SDL axes as named values, SDL kept every value and axis N is
        /// the order's N. With as many as the named values that are not
        /// relative, DirectInput left the relative ones out (SDL passes over
        /// an axis whose DIPROP_RANGE it cannot set, and Microsoft's
        /// SetProperty page says the range is read-only on some devices):
        /// the SDL axes are the rest in order, and the relative ones follow
        /// them as axes of PadForge's own (<see cref="LeftOutRelative"/>). -1
        /// for no value.</summary>
        internal static int ValueOfAxis(Layout layout, int axis, int sdlAxisCount)
        {
            if (axis < 0) return -1;
            bool leftOut = sdlAxisCount >= 0 && sdlAxisCount != layout.Order.Length
                && sdlAxisCount == layout.OrderWithoutRelative.Length;
            if (!leftOut) return axis < layout.Order.Length ? layout.Order[axis] : -1;
            if (axis < layout.OrderWithoutRelative.Length) return layout.OrderWithoutRelative[axis];
            var extra = LeftOut(layout);
            int j = axis - layout.OrderWithoutRelative.Length;
            return j < extra.Count ? extra[j] : -1;
        }

        private static List<int> LeftOut(Layout layout)
        {
            var extra = new List<int>();
            foreach (int v in layout.Order)
                if (layout.Values[v].Relative) extra.Add(v);
            return extra;
        }

        /// <summary>How many relative values of the controller at
        /// <paramref name="devicePath"/> SDL's DirectInput left out, which
        /// PadForge lists as axes after SDL's own (SdlDeviceWrapper's device
        /// objects), or 0. Reads the device's report descriptor through Raw
        /// Input when no report has come yet.</summary>
        public static int LeftOutRelative(string devicePath, int sdlAxisCount)
        {
            var layout = LayoutForPath(devicePath);
            if (layout == null || sdlAxisCount == layout.Order.Length
                || sdlAxisCount != layout.OrderWithoutRelative.Length) return 0;
            return LeftOut(layout).Count;
        }

        /// <summary>The layout of the controller at a path, built from the
        /// Raw Input device list when no report of it has come yet.</summary>
        private static Layout LayoutForPath(string devicePath)
        {
            if (string.IsNullOrEmpty(devicePath)) return null;
            if (s_byPath.TryGetValue(devicePath, out var known)) return known;
            try
            {
                uint count = 0;
                uint size = (uint)Marshal.SizeOf<RawDeviceListEntry>();
                if (GetRawInputDeviceList(null, ref count, size) != 0 || count == 0) return null;
                var list = new RawDeviceListEntry[count];
                if (GetRawInputDeviceList(list, ref count, size) == unchecked((uint)-1)) return null;
                for (int i = 0; i < count; i++)
                {
                    if (list[i].Type != RIM_TYPEHID) continue;
                    if (!string.Equals(DeviceName(list[i].Handle), devicePath, StringComparison.OrdinalIgnoreCase)) continue;
                    return s_byHandle.GetOrAdd(list[i].Handle, LayoutOf);
                }
            }
            catch
            {
            }
            return null;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RawDeviceListEntry
        {
            public IntPtr Handle;
            public uint Type;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetRawInputDeviceList([Out] RawDeviceListEntry[] list, ref uint count, uint size);

        /// <summary>Adds a report's movements to a layout's totals: the
        /// testable part of <see cref="Process"/>. <paramref name="read"/>
        /// gives a value's raw number in the report, or null where the report
        /// does not carry it.</summary>
        internal static void Add(Layout layout, Func<Value, uint?> read)
        {
            for (int i = 0; i < layout.Values.Length; i++)
            {
                var v = layout.Values[i];
                if (!v.Relative) continue;
                uint? raw = read(v);
                if (raw is not uint r) continue;
                long movement = SignExtend(r, v.BitSize, v.LogicalMin);
                if (movement != 0) Interlocked.Add(ref layout.Totals[i], movement);
            }
        }

        /// <summary>A layout built from values, for a test and for
        /// <see cref="LayoutOf"/>.</summary>
        internal static Layout Build(string path, Value[] values) => new()
        {
            Path = path,
            Values = values,
            Totals = new long[values.Length],
            Order = SdlOrder(values, withoutRelative: false),
            OrderWithoutRelative = SdlOrder(values, withoutRelative: true),
        };

        /// <summary>Registers a layout by its path, for a test.</summary>
        internal static void RegisterForTest(Layout layout) => s_byPath[layout.Path] = layout;

        /// <summary>Drops a path's layout, for a test.</summary>
        internal static void ForgetForTest(string path) => s_byPath.TryRemove(path, out _);

        /// <summary>One WM_INPUT record of a HID device: when it is a game
        /// controller with relative values, each report's movements join the
        /// totals. Pump thread only.</summary>
        internal static void Process(IntPtr hDevice, IntPtr rawHid)
        {
            var layout = s_byHandle.GetOrAdd(hDevice, LayoutOf);
            if (layout == null) return;
            int sizeHid = Marshal.ReadInt32(rawHid);
            int count = Marshal.ReadInt32(rawHid, 4);
            if (sizeHid <= 0 || count <= 0) return;
            IntPtr reports = IntPtr.Add(rawHid, 8);
            for (int r = 0; r < count; r++)
            {
                IntPtr report = IntPtr.Add(reports, r * sizeHid);
                Add(layout, v =>
                {
                    uint status = HidP_GetUsageValue(HidP_Input, v.Page, v.LinkCollection, v.Usage,
                        out uint raw, layout.Preparsed, report, (uint)sizeHid);
                    return status == HIDP_STATUS_SUCCESS ? raw : null;
                });
            }
        }

        /// <summary>Frees what the layouts hold, when the listener stops and
        /// its pump has left.</summary>
        internal static void Clear(bool freeBuffers)
        {
            foreach (var layout in s_byHandle.Values)
                if (freeBuffers && layout?.Preparsed is IntPtr p && p != IntPtr.Zero)
                    Marshal.FreeHGlobal(p);
            s_byHandle.Clear();
            s_byPath.Clear();
        }

        /// <summary>The layout of a Raw Input handle: its path, its input
        /// values from its preparsed data, or null when it is no joystick,
        /// game pad or multi-axis controller or has no relative value.</summary>
        private static Layout LayoutOf(IntPtr hDevice)
        {
            IntPtr preparsed = IntPtr.Zero;
            try
            {
                if (!IsGameController(hDevice)) return null;
                string path = DeviceName(hDevice);
                if (string.IsNullOrEmpty(path)) return null;
                preparsed = Preparsed(hDevice);
                if (preparsed == IntPtr.Zero) return null;

                var caps = new byte[64];
                if (HidP_GetCaps(preparsed, caps) != HIDP_STATUS_SUCCESS) { Marshal.FreeHGlobal(preparsed); return null; }
                ushort valueCapCount = BitConverter.ToUInt16(caps, 48);
                if (valueCapCount == 0) { Marshal.FreeHGlobal(preparsed); return null; }
                var buffer = new byte[valueCapCount * ValueCapsSize];
                ushort length = valueCapCount;
                if (HidP_GetValueCaps(HidP_Input, buffer, ref length, preparsed) != HIDP_STATUS_SUCCESS)
                {
                    Marshal.FreeHGlobal(preparsed);
                    return null;
                }

                var values = new List<Value>();
                bool anyRelative = false;
                for (int i = 0; i < length; i++)
                {
                    int o = i * ValueCapsSize;
                    ushort page = BitConverter.ToUInt16(buffer, o);
                    byte reportId = buffer[o + 2];
                    ushort link = BitConverter.ToUInt16(buffer, o + 6);
                    bool isRange = buffer[o + 12] != 0;
                    bool absolute = buffer[o + 15] != 0;
                    int bitSize = BitConverter.ToUInt16(buffer, o + 18);
                    int logicalMin = BitConverter.ToInt32(buffer, o + 40);
                    ushort usageMin = BitConverter.ToUInt16(buffer, o + 56);
                    ushort usageMax = isRange ? BitConverter.ToUInt16(buffer, o + 58) : usageMin;
                    for (int u = usageMin; u <= usageMax; u++)
                    {
                        values.Add(new Value(page, (ushort)u, reportId, link, bitSize, logicalMin, !absolute));
                        anyRelative |= !absolute;
                    }
                }
                if (!anyRelative) { Marshal.FreeHGlobal(preparsed); return null; }
                var layout = Build(path, values.ToArray());
                layout.Preparsed = preparsed;
                s_byPath[path] = layout;
                return layout;
            }
            catch
            {
                if (preparsed != IntPtr.Zero) Marshal.FreeHGlobal(preparsed);
                return null;
            }
        }

        private static bool IsGameController(IntPtr hDevice)
        {
            uint size = 32;
            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                Marshal.WriteInt32(buffer, (int)size);
                if (GetRawInputDeviceInfoW(hDevice, RIDI_DEVICEINFO, buffer, ref size) == unchecked((uint)-1)) return false;
                if (Marshal.ReadInt32(buffer, 4) != RIM_TYPEHID) return false;
                // RID_DEVICE_INFO_HID: dwVendorId, dwProductId, dwVersionNumber,
                // usUsagePage, usUsage.
                ushort page = (ushort)Marshal.ReadInt16(buffer, 20);
                ushort usage = (ushort)Marshal.ReadInt16(buffer, 22);
                return page == 0x01 && usage is 0x04 or 0x05 or 0x08;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static string DeviceName(IntPtr hDevice)
        {
            uint size = 0;
            GetRawInputDeviceInfoW(hDevice, RIDI_DEVICENAME, IntPtr.Zero, ref size);
            if (size == 0) return null;
            IntPtr buffer = Marshal.AllocHGlobal((int)size * 2);
            try
            {
                if (GetRawInputDeviceInfoW(hDevice, RIDI_DEVICENAME, buffer, ref size) == unchecked((uint)-1)) return null;
                return Marshal.PtrToStringUni(buffer);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static IntPtr Preparsed(IntPtr hDevice)
        {
            uint size = 0;
            GetRawInputDeviceInfoW(hDevice, RIDI_PREPARSEDDATA, IntPtr.Zero, ref size);
            if (size == 0) return IntPtr.Zero;
            IntPtr data = Marshal.AllocHGlobal((int)size);
            if (GetRawInputDeviceInfoW(hDevice, RIDI_PREPARSEDDATA, data, ref size) == unchecked((uint)-1))
            {
                Marshal.FreeHGlobal(data);
                return IntPtr.Zero;
            }
            return data;
        }

        private const int HidP_Input = 0;
        private const uint HIDP_STATUS_SUCCESS = 0x00110000;
        private const uint RIDI_PREPARSEDDATA = 0x20000005;
        private const uint RIDI_DEVICENAME = 0x20000007;
        private const uint RIDI_DEVICEINFO = 0x2000000b;
        private const int RIM_TYPEHID = 2;
        private const int ValueCapsSize = 72;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetRawInputDeviceInfoW(IntPtr hDevice, uint uiCommand, IntPtr pData, ref uint pcbSize);

        [DllImport("hid.dll")]
        private static extern uint HidP_GetCaps(IntPtr preparsedData, [Out] byte[] capabilities);

        [DllImport("hid.dll")]
        private static extern uint HidP_GetValueCaps(int reportType, [Out] byte[] valueCaps, ref ushort valueCapsLength, IntPtr preparsedData);

        [DllImport("hid.dll")]
        private static extern uint HidP_GetUsageValue(int reportType, ushort usagePage, ushort linkCollection, ushort usage,
            out uint usageValue, IntPtr preparsedData, IntPtr report, uint reportLength);
    }
}
