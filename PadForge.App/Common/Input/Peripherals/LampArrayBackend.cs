using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// One HID LampArray collection as its scan described it (#494): where
    /// it is, what it says of itself in its attributes report, the report
    /// IDs it chose, and how its lamps take a color. The linker sees it as
    /// a <see cref="DirectUnit"/>. This is what the worker paints from.
    /// </summary>
    internal sealed record LampArrayUnit(string Path, ushort VendorId, ushort ProductId)
    {
        public Guid ContainerId { get; init; }
        public string Name { get; init; }

        /// <summary>LampArrayKind (HID Usage Tables 1.6, 26.2.1): 1 a
        /// keyboard, 2 a mouse, any other value another kind of device.</summary>
        public int Kind { get; init; }
        public int LampCount { get; init; }
        public uint MinUpdateIntervalUs { get; init; }

        public byte AttributesId { get; init; }
        public byte RequestId { get; init; }
        public byte ResponseId { get; init; }
        public byte RangeId { get; init; }
        public byte ControlId { get; init; }

        /// <summary>False for a device with no control report, which has no
        /// effect of its own to be claimed from or handed back to.</summary>
        public bool HasControl { get; init; }
        public int FeatureLength { get; init; }

        /// <summary>The lamps one multi update report of the device sets,
        /// 0 for a device with none (<see cref="LampArrayLayout.MultiUpdateSlots"/>).</summary>
        public int MultiUpdateSlots { get; init; }
        public bool WindowsLit { get; init; }

        /// <summary>The multi update report's ID when PadForge can send it,
        /// 0 otherwise (<see cref="LampArrayLayout.MultiId"/>, #557).</summary>
        public byte MultiId { get; init; }

        /// <summary>The width of InputBinding in the lamp response
        /// (<see cref="LampArrayLayout.InputBindingBits"/>).</summary>
        public int InputBindingBits { get; init; }

        /// <summary>The lamp response describes each lamp where PadForge
        /// reads it (<see cref="LampArrayLayout.DescribesLamps"/>).</summary>
        public bool DescribesLamps { get; init; }

        /// <summary>Each lamp's level counts in lamp order, for painting
        /// lamp by lamp (#557). Null until the lamps are read, and for a
        /// unit painted blind.</summary>
        public IReadOnlyList<LampLevels> LampLevels { get; init; }

        /// <summary>The unit's lights (#557), from its lamp descriptions,
        /// with the names Map Key Lights learned for its model
        /// (<see cref="LearnedKeyMaps"/>). Null until the lamps are read,
        /// and for a unit that does not describe them.</summary>
        public LightMap Map { get; init; }

        /// <summary>The lights as the lamp descriptions give them, before
        /// any learned name, kept so a name learned later can be applied.</summary>
        public LightMap DeviceMap { get; init; }

        /// <summary>The model's key for learned names: vendor, product,
        /// LampArrayKind and lamp count, which together pick out one
        /// keyboard's lamp numbering.</summary>
        public string Product => string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"lamparray:{VendorId:X4}:{ProductId:X4}:{Kind}:{LampCount}");

        /// <summary>How the lamps take a color, in runs
        /// (<see cref="LampArrayProtocol.BuildRuns"/>). Null until the
        /// lamps have been read, which the first claim does, and for as
        /// long as they could not be: such a unit is painted blind
        /// (<see cref="LampArrayProtocol.Frames"/>).</summary>
        public IReadOnlyList<LampRun> Runs { get; init; }

        public int SettleMs => LampArrayProtocol.SettleMs(MinUpdateIntervalUs);
        public int PaintIntervalMs => LampArrayProtocol.PaintIntervalMs(MinUpdateIntervalUs);
    }

    /// <summary>What Windows' HID parser holds for a LampArray collection,
    /// read with no report sent to the device. The worker reaches the
    /// parser through this, so a bench scripts it.</summary>
    internal interface ILampArrayDescriptors
    {
        /// <summary>The collection's reports, when they are the ones
        /// PadForge drives. Null with <paramref name="rejection"/> saying
        /// why for a device that is not driven, and null with no reason when
        /// the collection could not be read now, which says nothing about
        /// the device.</summary>
        LampArrayLayout Read(string path, out string rejection);

        /// <summary>Whether the parser reads an attributes report the way
        /// <see cref="LampArrayProtocol.TryParseAttributes"/> did. Null
        /// when the collection could not be read now.</summary>
        bool? AttributesAgree(string path, LampArrayLayout layout, byte[] report, LampArrayAttributes parsed);

        /// <summary>The device's product string, or null. The one read
        /// here that goes to the device, a string request.</summary>
        string ProductName(string path);
    }

    /// <summary>
    /// The descriptor of a LampArray collection through Windows' HID parser
    /// (#494). The collection is opened with no access right, and nothing
    /// here sends it a report: the value fields and the report builds come
    /// from the descriptor Windows already parsed.
    ///
    /// <para>The descriptor is the device's, so PadForge does not take its
    /// reports on faith. <see cref="Read"/> finds each report by the usage
    /// of its logical collection and checks the fields it uses
    /// (<see cref="LampArrayProtocol.CheckShape"/>), then has the parser
    /// build each report PadForge sends or reads from known values and
    /// compares the bytes with its own frames
    /// (<see cref="LampArrayProtocol.CrossCheck"/>).
    /// <see cref="AttributesAgree"/> does the same from the read side for
    /// the attributes report, whose fields are constants no host sets.</para>
    /// </summary>
    internal sealed class LampArrayDescriptors : ILampArrayDescriptors
    {
        private const int HidP_Output = 1;

        private readonly Func<string, SafeFileHandle> _open;

        public LampArrayDescriptors()
            : this(null) { }

        /// <summary>Test seam: the open behind <see cref="OpenQueryOnly"/>,
        /// so a test counts the opens the test host's refusal lets through.</summary>
        internal LampArrayDescriptors(Func<string, SafeFileHandle> open) => _open = open ?? OpenCollection;

        public LampArrayLayout Read(string path, out string rejection)
        {
            rejection = null;
            using var handle = OpenQueryOnly(path);
            if (handle == null) return null;
            IntPtr preparsed = IntPtr.Zero;
            try
            {
                if (!SonyHeadsetHid.HidD_GetPreparsedData(handle, out preparsed)) return null;
                if (SonyHeadsetHid.HidP_GetCaps(preparsed, out var caps) != SonyHeadsetHid.HIDP_STATUS_SUCCESS) return null;

                var fields = new List<LampValueCap>();
                Add(fields, SonyHeadsetHid.GetValueCaps(SonyHeadsetHid.HidP_Feature, preparsed, caps.NumberFeatureValueCaps), output: false);
                Add(fields, SonyHeadsetHid.GetValueCaps(HidP_Output, preparsed, caps.NumberOutputValueCaps), output: true);

                var layout = LampArrayProtocol.CheckShape(fields, caps.FeatureReportByteLength, out rejection);
                if (layout == null)
                {
                    rejection += ". Its lighting fields: " + LampArrayProtocol.DescribeCaps(fields);
                    return null;
                }
                IntPtr parsed = preparsed;
                LampArrayProtocol.ParserBuild build = (reportId, link, values) => Build(parsed, layout.FeatureLength, reportId, link, values);
                rejection = LampArrayProtocol.CrossCheck(layout, build);
                if (rejection != null) return null;
                // Lamp by lamp (#557): the descriptions and the multi update
                // are used only where the parser lays them out as PadForge
                // reads and writes them. Either miss leaves the device to
                // one color at a time.
                bool describes = LampArrayProtocol.CheckDescriptions(layout, build);
                bool multi = LampArrayProtocol.CheckMulti(layout, fields,
                    (reportId, link, set) => BuildFields(parsed, layout.FeatureLength, reportId, link, set));
                return layout with
                {
                    DescribesLamps = describes,
                    MultiId = multi ? layout.MultiId : (byte)0,
                    MultiLink = multi ? layout.MultiLink : (ushort)0,
                };
            }
            catch
            {
                rejection = null;
                return null;
            }
            finally
            {
                if (preparsed != IntPtr.Zero) SonyHeadsetHid.HidD_FreePreparsedData(preparsed);
            }
        }

        public bool? AttributesAgree(string path, LampArrayLayout layout, byte[] report, LampArrayAttributes parsed)
        {
            if (layout == null || report == null) return false;
            using var handle = OpenQueryOnly(path);
            if (handle == null) return null;
            IntPtr preparsed = IntPtr.Zero;
            try
            {
                if (!SonyHeadsetHid.HidD_GetPreparsedData(handle, out preparsed)) return null;
                // The parser takes a report at the collection's own length
                // and nothing else.
                byte[] sized = report;
                if (sized.Length != layout.FeatureLength)
                {
                    sized = new byte[layout.FeatureLength];
                    Array.Copy(report, sized, Math.Min(report.Length, sized.Length));
                }
                return Value(preparsed, layout.AttributesLink, LampArrayProtocol.LampCountUsage, sized) == (uint)parsed.LampCount
                       && Value(preparsed, layout.AttributesLink, LampArrayProtocol.LampArrayKindUsage, sized) == parsed.Kind
                       && Value(preparsed, layout.AttributesLink, LampArrayProtocol.MinUpdateIntervalUsage, sized) == parsed.MinUpdateIntervalUs;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (preparsed != IntPtr.Zero) SonyHeadsetHid.HidD_FreePreparsedData(preparsed);
            }
        }

        public string ProductName(string path)
        {
            using var handle = OpenQueryOnly(path);
            if (handle == null) return null;
            try
            {
                string name = VendorHidRuntime.ReadProductString(handle);
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>The collection with no access right, the open the walk
        /// itself makes (HidCollectionRuntime.Probe): enough for everything
        /// the HID class driver holds, and nothing a second writer could
        /// notice. Every read of this class opens the collection here and
        /// nowhere else, so the test host's refusal
        /// (<see cref="RealHidAccess"/>) covers them all: each then answers
        /// as for a collection that cannot be read now.</summary>
        private SafeFileHandle OpenQueryOnly(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            // The test host opens no device (RealHidAccess).
            if (RealHidAccess.Refused) return null;
            var handle = _open(path);
            if (handle != null && !handle.IsInvalid) return handle;
            handle?.Dispose();
            return null;
        }

        private static SafeFileHandle OpenCollection(string path)
            => SonyHeadsetHid.CreateFile(path, 0,
                SonyHeadsetHid.FILE_SHARE_READ | SonyHeadsetHid.FILE_SHARE_WRITE,
                IntPtr.Zero, SonyHeadsetHid.OPEN_EXISTING, 0, IntPtr.Zero);

        private static void Add(List<LampValueCap> fields, SonyHeadsetHid.HIDP_VALUE_CAPS[] caps, bool output)
        {
            foreach (var cap in caps)
                fields.Add(new LampValueCap(output, cap.ReportID, cap.LinkCollection, cap.LinkUsagePage, cap.LinkUsage,
                    cap.UsagePage, cap.UsageMin, cap.IsRange ? cap.UsageMax : cap.UsageMin, cap.BitSize, cap.ReportCount)
                {
                    DataIndex = cap.DataIndexMin,
                });
        }

        /// <summary>One report as the parser lays it out: zeroed for its
        /// ID, then each usage set to its value. No device I/O. Null when
        /// the parser refuses a step.</summary>
        private static byte[] Build(IntPtr preparsed, int length, byte reportId, ushort link,
            IReadOnlyList<(ushort Usage, uint Value)> values)
        {
            var report = new byte[length];
            if (HidP_InitializeReportForID(SonyHeadsetHid.HidP_Feature, reportId, preparsed, report, (uint)report.Length)
                != SonyHeadsetHid.HIDP_STATUS_SUCCESS)
                return null;
            foreach (var (usage, value) in values)
            {
                if (HidP_SetUsageValue(SonyHeadsetHid.HidP_Feature, LampArrayProtocol.UsagePage, link, usage, value,
                        preparsed, report, (uint)report.Length) != SonyHeadsetHid.HIDP_STATUS_SUCCESS)
                    return null;
            }
            return report;
        }

        /// <summary>One report built by the parser from single values,
        /// value arrays and fields by data index (#557). No device I/O.
        /// Null when the parser refuses a step.</summary>
        private static byte[] BuildFields(IntPtr preparsed, int length, byte reportId, ushort link,
            IReadOnlyList<LampArrayProtocol.ParserField> fields)
        {
            var report = new byte[length];
            if (HidP_InitializeReportForID(SonyHeadsetHid.HidP_Feature, reportId, preparsed, report, (uint)report.Length)
                != SonyHeadsetHid.HIDP_STATUS_SUCCESS)
                return null;
            foreach (var field in fields)
            {
                uint status;
                if (field.Array != null)
                    status = HidP_SetUsageValueArray(SonyHeadsetHid.HidP_Feature, LampArrayProtocol.UsagePage, link, field.Usage,
                        field.Array, (ushort)field.Array.Length, preparsed, report, (uint)report.Length);
                else if (field.DataIndex >= 0)
                {
                    var data = new[] { new HIDP_DATA { DataIndex = (ushort)field.DataIndex, RawValue = field.Value } };
                    uint count = 1;
                    status = HidP_SetData(SonyHeadsetHid.HidP_Feature, data, ref count, preparsed, report, (uint)report.Length);
                }
                else
                    status = HidP_SetUsageValue(SonyHeadsetHid.HidP_Feature, LampArrayProtocol.UsagePage, link, field.Usage,
                        field.Value, preparsed, report, (uint)report.Length);
                if (status != SonyHeadsetHid.HIDP_STATUS_SUCCESS) return null;
            }
            return report;
        }

        /// <summary>HIDP_DATA (hidpi.h): a data index and its raw value,
        /// the union's ULONG member.</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct HIDP_DATA
        {
            public ushort DataIndex;
            public ushort Reserved;
            public uint RawValue;
        }

        [DllImport("hid.dll")]
        private static extern uint HidP_SetUsageValueArray(int reportType, ushort usagePage,
            ushort linkCollection, ushort usage, byte[] usageValue, ushort usageValueByteLength,
            IntPtr preparsedData, [In, Out] byte[] report, uint reportLength);

        [DllImport("hid.dll")]
        private static extern uint HidP_SetData(int reportType, [In] HIDP_DATA[] dataList, ref uint dataLength,
            IntPtr preparsedData, [In, Out] byte[] report, uint reportLength);

        /// <summary>One field of a report as the parser reads it, or null
        /// when it cannot.</summary>
        private static uint? Value(IntPtr preparsed, ushort link, ushort usage, byte[] report)
            => SonyHeadsetHid.HidP_GetUsageValue(SonyHeadsetHid.HidP_Feature, LampArrayProtocol.UsagePage, link, usage,
                   out uint value, preparsed, report, (uint)report.Length) == SonyHeadsetHid.HIDP_STATUS_SUCCESS
                ? value
                : null;

        // hidpi.h (Windows SDK 10.0.26100.0, shared/hidpi.h:545-551 and
        // 1123-1132). Both work on the caller's buffer alone.
        [DllImport("hid.dll")]
        private static extern uint HidP_InitializeReportForID(int reportType, byte reportId,
            IntPtr preparsedData, [In, Out] byte[] report, uint reportLength);

        [DllImport("hid.dll")]
        private static extern uint HidP_SetUsageValue(int reportType, ushort usagePage,
            ushort linkCollection, ushort usage, uint usageValue,
            IntPtr preparsedData, [In, Out] byte[] report, uint reportLength);
    }

    /// <summary>
    /// The container a LampArray unit is matched to its rows by (#494).
    ///
    /// <para>It is the container of the collection's own device node, the
    /// lookup the peripheral host makes for a row
    /// (AudioPassthroughService.DevicePathContainerId). One device needs
    /// more. Logitech's LampArray driver, which Windows Update installs,
    /// puts a slot node under a LIGHTSPEED receiver for each paired device,
    /// "USB\VID_046D&amp;PID_C54D&amp;LAMPARRAY\..._SLOT00" on the bench,
    /// and the LampArray collection Windows lights is a virtual HID device
    /// under that slot. The slot nodes report the receiver's container.
    /// Whether the virtual child does is not known: none was present on
    /// the bench. A virtual device that reports no container, or the PC's
    /// own, would be matched to the laptop's keyboard or to nothing, so for
    /// a collection under such a slot the container is then taken from the
    /// first ancestor above the slot that is a USB device,
    /// "USB\VID_xxxx&amp;PID_xxxx\serial", the receiver, whose rows the
    /// unit belongs to.</para>
    /// </summary>
    internal static class LampArrayContainers
    {
        /// <summary>One device node above a collection: its instance ID and
        /// the container it reports, Guid.Empty when it reports none.</summary>
        internal readonly record struct DeviceNode(string InstanceId, Guid Container);

        /// <summary>The container of the collection at an interface path,
        /// or Guid.Empty while it is not known.</summary>
        internal static Guid For(string path)
            => Decide(AudioPassthroughService.DevicePathContainerId(path), () => Ancestors(path));

        /// <summary>The rule, apart from the configuration manager calls
        /// behind it. <paramref name="plain"/> is the collection's own
        /// container, and <paramref name="ancestors"/> lists the nodes above
        /// it, the nearest first. It is asked only when the plain answer is
        /// no container or the PC's own.</summary>
        internal static Guid Decide(Guid plain, Func<IReadOnlyList<DeviceNode>> ancestors)
        {
            if (plain != Guid.Empty && plain != PeripheralLinker.LocalMachineContainer) return plain;
            IReadOnlyList<DeviceNode> chain;
            try { chain = ancestors?.Invoke(); }
            catch { chain = null; }
            if (chain == null) return plain;

            int slot = -1;
            for (int i = 0; i < chain.Count && slot < 0; i++)
                if (IsLogitechLampArraySlot(chain[i].InstanceId)) slot = i;
            if (slot < 0) return plain;
            for (int i = slot + 1; i < chain.Count; i++)
            {
                if (!IsUsbDevice(chain[i].InstanceId)) continue;
                return chain[i].Container != Guid.Empty ? chain[i].Container : plain;
            }
            return plain;
        }

        /// <summary>A slot node of Logitech's LampArray driver: a USB node
        /// of vendor 046D whose device ID ends in LAMPARRAY.</summary>
        internal static bool IsLogitechLampArraySlot(string instanceId)
        {
            string device = DeviceIdOf(instanceId, "USB");
            return device != null
                   && device.StartsWith("VID_046D", StringComparison.OrdinalIgnoreCase)
                   && device.EndsWith("LAMPARRAY", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>A USB device node, "USB\VID_xxxx&amp;PID_xxxx\serial":
        /// the device itself, and neither one of its interfaces
        /// ("&amp;MI_01") nor a node a driver hung beneath it.</summary>
        internal static bool IsUsbDevice(string instanceId)
        {
            string device = DeviceIdOf(instanceId, "USB");
            if (device == null || device.Length != 17) return false;
            if (!device.StartsWith("VID_", StringComparison.OrdinalIgnoreCase)
                || string.Compare(device, 8, "&PID_", 0, 5, StringComparison.OrdinalIgnoreCase) != 0)
                return false;
            for (int i = 4; i < 8; i++) if (!Uri.IsHexDigit(device[i])) return false;
            for (int i = 13; i < 17; i++) if (!Uri.IsHexDigit(device[i])) return false;
            return true;
        }

        /// <summary>The middle part of an instance ID,
        /// "enumerator\device\instance", when the enumerator is the one
        /// asked for. Null otherwise.</summary>
        private static string DeviceIdOf(string instanceId, string enumerator)
        {
            if (string.IsNullOrEmpty(instanceId)) return null;
            string[] parts = instanceId.Split('\\');
            return parts.Length == 3 && parts[0].Equals(enumerator, StringComparison.OrdinalIgnoreCase) ? parts[1] : null;
        }

        /// <summary>The device nodes above a collection, the nearest first,
        /// each with its container, or null when the collection's own node
        /// could not be found. Read-only configuration manager calls.</summary>
        internal static List<DeviceNode> Ancestors(string path)
        {
            try
            {
                var instanceKey = new DEVPROPKEY { fmtid = new Guid("78C34FC8-104A-4ACA-9EA4-524D52996E57"), pid = 256 };
                var buffer = new byte[1024];
                uint size = (uint)buffer.Length;
                if (CM_Get_Device_Interface_PropertyW(path, ref instanceKey, out _, buffer, ref size, 0) != 0) return null;
                string instanceId = Encoding.Unicode.GetString(buffer, 0, (int)size).TrimEnd('\0');
                if (CM_Locate_DevNodeW(out uint node, instanceId, 0) != 0) return null;

                var chain = new List<DeviceNode>();
                var containerKey = new DEVPROPKEY { fmtid = new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), pid = 2 };
                // A device tree is a few nodes deep. The bound only keeps a
                // loop that could not end from running on.
                for (int depth = 0; depth < 32 && CM_Get_Parent(out uint parent, node, 0) == 0; depth++)
                {
                    node = parent;
                    var id = new StringBuilder(256);
                    if (CM_Get_Device_IDW(node, id, id.Capacity, 0) != 0) break;
                    var guid = new byte[16];
                    uint guidSize = 16;
                    Guid container = CM_Get_DevNode_PropertyW(node, ref containerKey, out _, guid, ref guidSize, 0) == 0
                                     && guidSize == 16
                        ? new Guid(guid)
                        : Guid.Empty;
                    chain.Add(new DeviceNode(id.ToString(), container));
                }
                return chain;
            }
            catch
            {
                return null;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DEVPROPKEY
        {
            public Guid fmtid;
            public uint pid;
        }

        // DEVPKEY_Device_InstanceId and DEVPKEY_Device_ContainerId above
        // are the keys AudioPassthroughService reads a container by.
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_Interface_PropertyW(string deviceInterface, ref DEVPROPKEY propertyKey,
            out uint propertyType, byte[] buffer, ref uint bufferSize, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Get_Parent(out uint parent, uint devInst, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_IDW(uint devInst, StringBuilder buffer, int bufferLength, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DEVPROPKEY propertyKey,
            out uint propertyType, byte[] buffer, ref uint bufferSize, uint flags);
    }

    /// <summary>
    /// The HID LampArray worker (#494): lights the devices Windows Dynamic
    /// Lighting drives, each on its own, with no vendor software. One
    /// thread, "PeripheralLampArray", owns every LampArray collection, so a
    /// scan, a claim and a color never interleave on one of them.
    ///
    /// <para>Scan. From the walk the host hands to <see cref="Rescan"/> it
    /// keeps the collections on usage page 0x59, usage 0x01, and describes
    /// each once: the descriptor through Windows' parser, with no report
    /// sent (<see cref="LampArrayDescriptors"/>), then one read of the
    /// attributes report for the lamp count, the kind and the update
    /// interval. A device whose reports are not the ones PadForge drives
    /// is named once in the diagnostics log and left alone. Nothing is
    /// written to a device nobody lights.</para>
    ///
    /// <para>Claim. A unit whose path a row links and a claim resolves on
    /// is opened for reading and writing. Its lamps are read first, once
    /// per arrival: the request for lamp 0, then one response per lamp,
    /// each checked for the lamp it names (HID Usage Tables 1.6, 26.8.2).
    /// Reads come before any write of control or color because the HUT's
    /// own sequence has them first (26.6) and because one firmware "NAKs
    /// GET_REPORT for a short window after a write"
    /// (acer-predator-helios-rgb protocol/PROTOCOL.md:212). Then the claim,
    /// AutonomousMode off, the wait the HUT promises the device (26.10.1),
    /// and the color in one range report per run.</para>
    ///
    /// <para>Hold. A new color is sent no sooner than the device's update
    /// interval after the last one, and the newest color is the one sent.
    /// The claim and the color go out again every <see cref="ReassertMs"/>,
    /// the one interval a reference names: a host for a Lenovo ITE 8258
    /// keyboard re-takes control and re-applies every four seconds, which
    /// "Keeps the firmware from timing out back to its autonomous default
    /// (white)" (aura-loq aura/cycle.py:173-176 and 194-196). A repeated
    /// claim is a no-op to a device that kept it (HID Usage Tables 1.6,
    /// 26.10.1).</para>
    ///
    /// <para>Hand back. When nothing claims the unit any more, on stop and
    /// on the crash path: AutonomousMode on, which returns the device to
    /// its own effect (OpenRGB Controllers/HIDLampArrayController/RGBController_HIDLampArray.cpp:434-444,
    /// aura-loq aura/controller.py:80-82, mistel-lights
    /// Sources/BaroccoHID/LampArrayClient.swift:142-146). A device with no
    /// control report has no effect to return to, so its lamps are turned
    /// off, their default state (HID Usage Tables 1.6, 26.11). A unit
    /// whose path was withdrawn because its vendor's program started is
    /// handed back like any other. While AutonomousMode is off, "No source
    /// other than the Host can modify Lamp state" (HID Usage Tables 1.6,
    /// 26.10.1), and the program, which speaks its vendor's own protocol,
    /// is such a source: on one device, once a host has taken the
    /// LampArray over, "the vendor path stops having any visible effect"
    /// (acer-predator-helios-rgb protocol/PROTOCOL.md:21-24).</para>
    ///
    /// <para>Windows. A unit Windows Dynamic Lighting lights is listed and
    /// never written to (<see cref="WindowsLightingState"/>): Windows
    /// repaints it, and a hand-back would switch Windows' own effect off,
    /// since a device in autonomous mode may ignore every update (HID Usage
    /// Tables 1.6, 26.10.1). A unit that turns Windows-lit while claimed is
    /// let go without a frame. The state is read again every
    /// <see cref="LitReadMs"/>, and once more right before each hand-back.
    /// A collection the scan cannot describe is not listed, so the rule
    /// that closes a device's own protocol while Windows lights it
    /// (<see cref="PeripheralLinker.ChooseLighting"/>) does not see it.
    /// Nothing on record says whether Windows lights such a collection in
    /// fact: the switch reads on for every collection nobody set, one
    /// Windows never took among them, and closing a HID++, Razer or
    /// SteelSeries path on that reading would darken a device that
    /// nothing else lights.</para>
    ///
    /// <para>The control report is never read back. Microsoft's firmware
    /// samples answer a GET for the attributes and the response alone
    /// (ArduinoHidForWindows src/Microsoft_HidLampArray.cpp:69-84), and
    /// real firmware stalls one for the control report (hp-omen-35l-rgb
    /// README.md:43). A stalled read blocks its caller for a time no
    /// source gives.</para>
    ///
    /// <para>A failed write follows the contract's rule
    /// (<see cref="IDirectUnitBackend"/>): the channel is closed, the unit
    /// is listed asleep, a walk is asked for once, and the unit is
    /// described again after 5, then 10, then 15 seconds, the HID++
    /// worker's backoff (HidppUnitProbe.Backoff). It leaves the list when
    /// its collection leaves the walk. A unit that has never answered its
    /// attributes read is not listed, and is asked again on the same
    /// backoff.</para>
    ///
    /// <para>A unit that was claimed when its write failed is owed its
    /// hand-back from then on. A device that missed one report and stayed
    /// on the bus still has AutonomousMode off, and keeps the last color
    /// until it loses power. So the unit stays on the crash path's record,
    /// and the hand-back goes out when it answers again with nothing
    /// claiming it, or at stop through a bounded write on a handle of its
    /// own. A claim that takes the unit anew ends the debt, and so does
    /// Windows lighting it.</para>
    ///
    /// <para>No step of a pass sleeps. Every wait there is a time the
    /// worker comes back at: the pause between two tries of a failed read,
    /// the wait between the claim and the first color, the update
    /// interval. A unit's lamps are read <see cref="LampsPerPass"/> to a
    /// pass. So a device that is slow to answer holds up no other unit's
    /// colors for longer than one such step takes. The stop path is the
    /// one place that sleeps: for a device with no control report it waits
    /// out what is left of one update interval, a second at most, before
    /// the report that turns the lamps off. Stop's own wait is longer than
    /// that. The crash path's is not, so there that report can go out
    /// inside the interval, where such a device may ignore it (HID Usage
    /// Tables 1.6, 26.7). HidD_SetFeature and HidD_GetFeature themselves
    /// take no timeout, which is why this family has a thread of its own
    /// and the crash path writes through a bounded call
    /// (<see cref="HidReportChannel.RunBounded"/>).</para>
    /// </summary>
    internal sealed class LampArrayBackend : IDirectUnitBackend
    {
        /// <summary>How often a lit unit's claim and color are sent again
        /// (aura-loq aura/cycle.py:176, KEEPALIVE = 4.0).</summary>
        internal const int ReassertMs = 4000;

        /// <summary>How often Windows' lighting switches are read again
        /// while a unit is present.</summary>
        internal const int LitReadMs = 1000;

        /// <summary>A read that fails is tried eight times in all, with a
        /// pause of 50 ms times the number of tries made so far, the rule
        /// of the host that found the window after a write in which its
        /// firmware refuses reads (acer-predator-helios-rgb
        /// tools/predator_rgb.py:108-118, 0 failures in 40 with it against
        /// about 1 in 10 without, protocol/PROTOCOL.md:212-214).</summary>
        internal const int ReadAttempts = 8;
        internal const int ReadRetryStepMs = 50;

        /// <summary>The most lamps read in one pass. A keyboard has
        /// hundreds, each a report of its own, so the read is made in
        /// pieces and the worker sees to its other units between two of
        /// them. The number is PadForge's own. A device counts on for "the
        /// next time the Host sends" its read, whenever that comes (HID
        /// Usage Tables 1.6, 26.8.2).</summary>
        internal const int LampsPerPass = 32;

        /// <summary>How long a unit that did not answer waits before it is
        /// described again: 5 seconds, then 10, then 15 at most, as a
        /// silent HID++ slot does.</summary>
        internal const int FirstRetryMs = 5000;
        internal const int MaxRetryMs = 15000;

        /// <summary>The worker's wait with nothing due sooner. A color
        /// change wakes it at once
        /// (<see cref="PeripheralOutputs.LightingChanged"/>).</summary>
        internal const int IdleTickMs = 100;

        /// <summary>How long a new worker waits for the one a timed-out
        /// Stop left inside a report, before it opens the collections
        /// anyway (HidppBackend.PredecessorJoinMs).</summary>
        internal const int PredecessorJoinMs = 5000;

        /// <summary>How long Stop waits for the worker to hand back and
        /// end.</summary>
        internal const int StopJoinMs = 3000;

        /// <summary>The crash path: how long it waits for the worker to
        /// hand back on its own channel, and how long each one-shot
        /// hand-back may take after that (HidppBackend.PanicJoinMs and its
        /// one-shot bound).</summary>
        internal const int PanicJoinMs = 250;
        internal const int OneShotMs = 200;

        /// <summary>
        /// Test seams: the descriptor reads, the two opens (with no access
        /// right for the scan's read, by the ladder for a claim), the
        /// container lookup, the read of Windows' lighting switch for one
        /// collection, the clock every interval here is measured on, the
        /// crash path's one-shot writer and its wait for the worker, the
        /// stop path's pause, the idle tick, the wait for a predecessor and
        /// Stop's own wait, so a bench drives the worker against scripted
        /// channels and a time it sets itself.
        /// </summary>
        internal sealed class Seams
        {
            public ILampArrayDescriptors Descriptors { get; init; }
            public Func<string, IHidReportChannel> OpenQuery { get; init; }
            public Func<string, IHidReportChannel> OpenClaim { get; init; }
            public Func<string, Guid> ContainerOf { get; init; }
            public Func<string, bool> WindowsLit { get; init; }
            public Func<long> Ticks { get; init; }
            public Func<string, byte[], int, bool> SetFeatureOnce { get; init; }
            public Action<int> Pause { get; init; }

            /// <summary>The crash path's wait for the worker to end, given
            /// the most it may take.</summary>
            public Func<Thread, int, bool> Join { get; init; }
            public int IdleTickMs { get; init; } = LampArrayBackend.IdleTickMs;
            public int PredecessorJoinMs { get; init; } = LampArrayBackend.PredecessorJoinMs;
            public int StopJoinMs { get; init; } = LampArrayBackend.StopJoinMs;
        }

        private enum Phase
        {
            /// <summary>Not claimed, and no channel open.</summary>
            Idle,
            /// <summary>Open, reading its lamps ahead of a claim.</summary>
            Reading,
            /// <summary>Claimed, waiting out the device's interval before
            /// the first color.</summary>
            Settling,
            /// <summary>Claimed and painted.</summary>
            Lit,
        }

        /// <summary>Everything the worker holds for one present LampArray
        /// collection. Worker thread only.</summary>
        private sealed class Lamp
        {
            /// <summary>The interface path as the walk first gave it, which
            /// is the unit's key.</summary>
            public string Path;
            public ushort VendorId;
            public ushort ProductId;

            /// <summary>Null until the collection answered its attributes
            /// read once. A unit is listed from then on.</summary>
            public LampArrayUnit Unit;

            /// <summary>Its descriptor is not one PadForge drives. Named
            /// once, and not asked again while it stays.</summary>
            public bool Rejected;
            public bool Asleep;
            public bool WindowsLit;

            // A description under way: the layout the parser gave, which
            // handle the attributes are being read on (0 none, 1 the one
            // with no access right, 2 the one a claim uses), the tries made
            // on it, and when the next is due.
            public LampArrayLayout Layout;
            public int Stage;
            public int Attempt;
            public long NextAt;
            public IHidReportChannel ProbeIo;

            // The backoff after a description or a write that failed.
            public int Misses;
            public long RetryAt;
            public bool MissNamed;

            // The claim.
            public IHidReportChannel Io;
            public Phase Phase;
            public bool LampsRead;
            public List<LampDescription> Descriptions;

            /// <summary>What each lamp shows after the last per-lamp paint
            /// (#557), or null when the unit was last painted one color.</summary>
            public int[] Shown;
            public int LampAttempt;
            public bool Rerequested;
            public long LampNextAt;
            public long ClaimedAt;
            public long SettleUntil;
            public bool Sent;
            public int SentRgb;
            public long SentAt;

            public bool Claimed => Phase == Phase.Settling || Phase == Phase.Lit;

            /// <summary>A hand-back is owed: the unit fell asleep while it
            /// was claimed, so its device may still have AutonomousMode
            /// off, or hold a color no effect of its own repaints. Paid
            /// when the unit answers again with nothing claiming it, at
            /// stop and on the crash path. Ended by a claim that takes the
            /// unit anew, by Windows lighting it, and by its collection
            /// leaving the walk.</summary>
            public bool Owed;

            /// <summary>Described, rejected, or asked once without an
            /// answer: nothing the first scan still has to find out.</summary>
            public bool Settled => Unit != null || Rejected || Misses > 0;

            public string Label => !string.IsNullOrWhiteSpace(Unit?.Name) ? "'" + Unit.Name + "'" : $"{VendorId:X4}:{ProductId:X4}";
        }

        /// <summary>What one pass changed, gathered so the snapshot, the
        /// crash record and the ask for a walk each go out once.</summary>
        private struct Pass
        {
            public long Now;
            /// <summary>The soonest anything is due, which bounds the wait.</summary>
            public long Due;
            public bool Units;
            public bool Claims;
            public bool Failed;

            public void DueAt(long when)
            {
                if (when < Due) Due = when;
            }
        }

        private static readonly (string Path, byte[] HandBack)[] NoClaims = Array.Empty<(string, byte[])>();

        /// <summary>The newest worker thread of any instance, which the
        /// next one waits for. Static because the collections it holds are
        /// the machine's.</summary>
        private static Thread s_lastWorker;

        private static readonly long s_clockStart = Stopwatch.GetTimestamp();

        private readonly ILampArrayDescriptors _descriptors;
        private readonly Func<string, IHidReportChannel> _openQuery;
        private readonly Func<string, IHidReportChannel> _openClaim;
        private readonly Func<string, Guid> _containerOf;
        private readonly Func<string, bool> _windowsLit;
        private readonly Func<long> _ticks;
        private readonly Func<long> _deadlineTicks;
        private readonly Func<string, byte[], int, bool> _setFeatureOnce;
        private readonly Action<int> _pause;
        private readonly Func<Thread, int, bool> _join;
        private readonly int _idleTickMs;
        private readonly int _predecessorJoinMs;
        private readonly int _stopJoinMs;
        private readonly AutoResetEvent _wake = new(false);

        private DirectBackendSnapshot _snapshot = DirectBackendSnapshot.NotScanned;

        /// <summary>The units holding a claim now and the ones owed a
        /// hand-back (<see cref="Lamp.Owed"/>), each with the report that
        /// hands it back, for the crash path, which cannot wait on this
        /// worker. Swapped whole.</summary>
        private (string Path, byte[] HandBack)[] _claimed = NoClaims;

        /// <summary>The last walk the host handed over, and how many it
        /// has: the worker scans when the count moved.</summary>
        private IReadOnlyList<HidCollectionInfo> _present;
        private int _walks;

        private Thread _thread;
        private volatile WorkerRun _run;
        private int _disposed;
        private int _passes;

        public LampArrayBackend()
            : this(null) { }

        internal LampArrayBackend(Seams seams)
        {
            seams ??= new Seams();
            _descriptors = seams.Descriptors ?? new LampArrayDescriptors();
            _openQuery = seams.OpenQuery ?? HidReportChannel.OpenFeatureOnly;
            _openClaim = seams.OpenClaim ?? HidReportChannel.Open;
            _containerOf = seams.ContainerOf ?? LampArrayContainers.For;
            _windowsLit = seams.WindowsLit ?? new WindowsLightingState().Lights;
            // The wait after a claim and the update interval are promises
            // to the device, some of them a few milliseconds long, and
            // Environment.TickCount64 steps by the system timer's period,
            // about 16 ms: two reads 3 ms apart on it can be microseconds
            // apart in fact. So they are measured on the stopwatch. The
            // crash path's deadline is a tick of Environment.TickCount64,
            // and is read against that.
            _ticks = seams.Ticks ?? (() => (long)Stopwatch.GetElapsedTime(s_clockStart).TotalMilliseconds);
            _deadlineTicks = seams.Ticks ?? (() => Environment.TickCount64);
            _setFeatureOnce = seams.SetFeatureOnce ?? HidReportChannel.SetFeatureOnce;
            _pause = seams.Pause ?? Thread.Sleep;
            _join = seams.Join ?? ((thread, ms) => thread.Join(ms));
            _idleTickMs = Math.Max(1, seams.IdleTickMs);
            _predecessorJoinMs = seams.PredecessorJoinMs;
            _stopJoinMs = seams.StopJoinMs;
        }

        public OutputFamily Family => OutputFamily.LampArrayUnit;

        public DirectBackendSnapshot Snapshot => Volatile.Read(ref _snapshot);

        public event Action SnapshotChanged;
        public event Action RescanRequested;

        /// <summary>Passes the worker has ended, so a bench waits on
        /// passes and never on time.</summary>
        internal int PassCount => Volatile.Read(ref _passes);

        internal bool WorkerAlive => _thread?.IsAlive == true;

        /// <summary>The paths on the crash path's record now.</summary>
        internal IReadOnlyList<string> ClaimedPaths
        {
            get
            {
                var claimed = Volatile.Read(ref _claimed);
                var paths = new string[claimed.Length];
                for (int i = 0; i < claimed.Length; i++) paths[i] = claimed[i].Path;
                return paths;
            }
        }

        public void Start()
        {
            if (Volatile.Read(ref _disposed) != 0 || _thread != null) return;
            var run = new WorkerRun();
            // A walk handed over before this start is of another run. The
            // host hands a new one on its first pass. The count is read
            // here and not by the worker, whose thread may first run after
            // that walk has come in: read there, the walk would look like
            // one already taken. It is read before the run is put in
            // charge, from when Rescan takes a walk, so none can fall
            // between the two and be lost.
            int walks = Volatile.Read(ref _walks);
            Volatile.Write(ref _present, null);
            Volatile.Write(ref _snapshot, DirectBackendSnapshot.NotScanned);
            _run = run;
            _thread = new Thread(() => Worker(run, walks)) { IsBackground = true, Name = "PeripheralLampArray" };
            _thread.Start();
            PeripheralOutputs.LightingChanged += Wake;
            LearnedKeyMaps.Changed += OnLearned;
        }

        public void Stop()
        {
            PeripheralOutputs.LightingChanged -= Wake;
            LearnedKeyMaps.Changed -= OnLearned;
            var thread = _thread;
            if (thread == null) return;
            var run = _run;
            if (run != null) run.Stop = true;
            Wake();
            try { thread.Join(_stopJoinMs); } catch { }
            _thread = null;
            _run = null;
            // The worker's own finally did this unless the join gave up on
            // it. A worker still inside a report writes no snapshot once
            // its run is no longer the one in charge.
            Volatile.Write(ref _snapshot, DirectBackendSnapshot.NotScanned);
        }

        public void Rescan(IReadOnlyList<HidCollectionInfo> present)
        {
            // A call that slips in beside a stop or after it is dropped.
            if (present == null || _run == null || Volatile.Read(ref _disposed) != 0) return;
            Volatile.Write(ref _present, present);
            Interlocked.Increment(ref _walks);
            Wake();
        }

        public void SignalStop()
        {
            var run = _run;
            if (run != null) run.Stop = true;
            Wake();
        }

        /// <summary>For the crash path and the process exit. The worker is
        /// told to stop, and its own finally hands back what it holds on
        /// its own channel. It gets <see cref="PanicJoinMs"/> for that and
        /// never past the deadline. Each unit still on record after that
        /// goes back through a write that opens a handle of its own, each
        /// bounded, until the deadline. A unit Windows lights at that
        /// moment is skipped, as the worker skips it.</summary>
        public void ReleaseClaimedNow(long deadline)
        {
            SignalStop();
            long left = deadline - _deadlineTicks();
            var thread = _thread;
            if (left > 0 && thread != null)
            {
                try { _join(thread, (int)Math.Min(PanicJoinMs, left)); } catch { }
            }
            foreach (var (path, handBack) in Interlocked.Exchange(ref _claimed, NoClaims))
            {
                left = deadline - _deadlineTicks();
                if (left <= 0) break;
                if (LitNow(path)) continue;
                try { _setFeatureOnce(path, handBack, (int)Math.Min(OneShotMs, left)); }
                catch { }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Stop();
            _wake.Dispose();
        }

        private void Wake()
        {
            try { _wake.Set(); } catch (ObjectDisposedException) { }
        }

        private void OnLearned(object sender, EventArgs e) => Wake();

        /// <summary>Whether a run is still the one in charge. A worker a
        /// timed-out Stop gave up on publishes nothing more.</summary>
        private bool InCharge(WorkerRun run) => ReferenceEquals(_run, run);

        private void Worker(WorkerRun run, int walksAtStart)
        {
            // A worker a timed-out Stop left inside a report still holds
            // its collections, and its finally hands its units back, so a
            // new worker waits for it before it opens anything, the bounded
            // join HidppBackend.Worker makes for its predecessor.
            var previous = Interlocked.Exchange(ref s_lastWorker, Thread.CurrentThread);
            if (previous != null && previous != Thread.CurrentThread && previous.IsAlive
                && !previous.Join(_predecessorJoinMs))
                PadForge.Engine.SdlDiagLog.WriteLine(
                    $"PERIPHERAL LampArray previous worker still running after {_predecessorJoinMs} ms, starting anyway");

            var lamps = new Dictionary<string, Lamp>(StringComparer.OrdinalIgnoreCase);
            try
            {
                int seenWalk = walksAtStart;
                int seenLearned = LearnedKeyMaps.Version;
                bool walked = false, scanned = false;
                long nextLitRead = long.MinValue;
                while (!run.Stop)
                {
                    var pass = new Pass { Now = _ticks() };
                    pass.Due = pass.Now + _idleTickMs;

                    int walk = Volatile.Read(ref _walks);
                    var present = Volatile.Read(ref _present);
                    if (walk != seenWalk && present != null)
                    {
                        seenWalk = walk;
                        walked = true;
                        ApplyWalk(lamps, present, ref pass);
                    }

                    foreach (var lamp in lamps.Values) Describe(run, lamp, ref pass);

                    // Names learned with Map Key Lights since the maps were
                    // made (#557) reach them, and the tab, here.
                    int learned = LearnedKeyMaps.Version;
                    if (learned != seenLearned)
                    {
                        seenLearned = learned;
                        Relearn(lamps);
                    }

                    if (pass.Now >= nextLitRead && AnyUnit(lamps))
                    {
                        ReadWindowsLit(lamps, ref pass);
                        nextLitRead = pass.Now + LitReadMs;
                    }
                    if (AnyUnit(lamps)) pass.DueAt(nextLitRead);

                    var links = PeripheralOutputs.Links;
                    foreach (var lamp in lamps.Values)
                        if (lamp.Unit != null && !lamp.Asleep) Paint(run, lamp, links, ref pass);

                    // The first scan is over once every collection of the
                    // first walk has answered, been turned down, or been
                    // asked once in vain. Until then a row keeps what it
                    // had on record.
                    if (!scanned && walked && AllSettled(lamps))
                    {
                        scanned = true;
                        pass.Units = true;
                    }
                    if (pass.Claims) PublishClaimed(run, lamps);
                    if (pass.Units) Publish(run, lamps, scanned);
                    if (pass.Failed)
                    {
                        // The device may have left with no row to say so.
                        try { RescanRequested?.Invoke(); } catch { }
                    }

                    Interlocked.Increment(ref _passes);
                    int wait = (int)Math.Clamp(pass.Due - pass.Now, 1, _idleTickMs);
                    // A Dispose whose Stop gave up its join releases the
                    // wake under a worker still inside a report, which ends
                    // here.
                    try { _wake.WaitOne(wait); }
                    catch (ObjectDisposedException) when (run.Stop) { break; }
                }
            }
            catch (Exception ex)
            {
                PadForge.Engine.SdlDiagLog.WriteLine("PERIPHERAL LampArray worker fault: " + ex.GetType().Name);
            }
            finally
            {
                foreach (var lamp in lamps.Values)
                {
                    // Each device goes back to its own effect before its
                    // channel closes, so stopping the engine leaves no
                    // keyboard frozen on the last color.
                    try { HandBackOnStop(lamp); } catch { }
                    Dispose(ref lamp.Io);
                    Dispose(ref lamp.ProbeIo);
                }
                if (InCharge(run))
                {
                    Volatile.Write(ref _claimed, NoClaims);
                    Volatile.Write(ref _snapshot, DirectBackendSnapshot.NotScanned);
                }
            }
        }

        /// <summary>Applies the learned names to every unit whose lamps
        /// were read, and publishes each map they change.</summary>
        private static void Relearn(Dictionary<string, Lamp> lamps)
        {
            foreach (var lamp in lamps.Values)
            {
                var unit = lamp.Unit;
                if (unit?.DeviceMap == null) continue;
                var map = LearnedKeyMaps.Apply(unit.DeviceMap);
                if (ReferenceEquals(map, unit.Map)) continue;
                lamp.Unit = unit with { Map = map };
                PeripheralLightMaps.Publish(new OutputPath(OutputFamily.LampArrayUnit, unit.Path), map);
            }
        }

        private static bool AnyUnit(Dictionary<string, Lamp> lamps)
        {
            foreach (var lamp in lamps.Values)
                if (lamp.Unit != null) return true;
            return false;
        }

        private static bool AllSettled(Dictionary<string, Lamp> lamps)
        {
            foreach (var lamp in lamps.Values)
                if (!lamp.Settled) return false;
            return true;
        }

        // ─────────────────────────────────────────────
        //  The scan
        // ─────────────────────────────────────────────

        /// <summary>Takes a walk in: a LampArray collection that is new
        /// gets a description, and one that left takes its unit off the
        /// list. Nothing is sent to a device that left, claimed or not:
        /// its handle leads nowhere.</summary>
        private void ApplyWalk(Dictionary<string, Lamp> lamps, IReadOnlyList<HidCollectionInfo> present, ref Pass pass)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var info in present)
            {
                if (info == null || string.IsNullOrEmpty(info.Path)) continue;
                if (info.UsagePage != LampArrayProtocol.UsagePage || info.Usage != LampArrayProtocol.LampArrayUsage) continue;
                seen.Add(info.Path);
                if (lamps.ContainsKey(info.Path)) continue;
                lamps[info.Path] = new Lamp
                {
                    Path = info.Path, VendorId = info.VendorId, ProductId = info.ProductId, RetryAt = pass.Now,
                };
            }

            List<string> gone = null;
            foreach (var path in lamps.Keys)
                if (!seen.Contains(path)) (gone ??= new List<string>()).Add(path);
            if (gone != null)
            {
                foreach (var path in gone)
                {
                    var lamp = lamps[path];
                    lamps.Remove(path);
                    PeripheralLightMaps.Remove(new OutputPath(OutputFamily.LampArrayUnit, path));
                    if (lamp.Claimed || lamp.Owed) pass.Claims = true;
                    Dispose(ref lamp.Io);
                    Dispose(ref lamp.ProbeIo);
                    if (lamp.Unit == null) continue;
                    pass.Units = true;
                    PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL LampArray {lamp.Label} left");
                }
            }

            // A container lookup that failed is not kept: each walk asks
            // again, and the unit takes the container once it answers.
            // Kept, it would leave the unit matched to no row for as long
            // as its collection stays.
            foreach (var lamp in lamps.Values)
            {
                if (lamp.Unit == null || lamp.Unit.ContainerId != Guid.Empty) continue;
                Guid container = ContainerOf(lamp.Path);
                if (container == Guid.Empty) continue;
                lamp.Unit = lamp.Unit with { ContainerId = container };
                pass.Units = true;
            }
        }

        /// <summary>
        /// Describes one collection, a step at a time: its descriptor
        /// through the parser, then one read of its attributes report. The
        /// read is tried on a handle opened with no access right, which
        /// asks Windows for nothing a second writer could notice, and then
        /// on the handle a claim uses. On each it is tried
        /// <see cref="ReadAttempts"/> times with the pause that grows by
        /// <see cref="ReadRetryStepMs"/>. A pause is a time to come back
        /// at, so this returns and the worker goes on with its other units.
        /// A collection that still does not answer is asked again after the
        /// backoff. It is run for a collection not described yet and for a
        /// unit that is asleep, which wakes when it answers.
        /// </summary>
        private void Describe(WorkerRun run, Lamp lamp, ref Pass pass)
        {
            if (lamp.Rejected || (lamp.Unit != null && !lamp.Asleep)) return;
            // A stopping worker asks nothing more of any collection, its
            // descriptor included.
            if (run.Stop) return;
            if (lamp.Stage == 0)
            {
                if (pass.Now < lamp.RetryAt)
                {
                    pass.DueAt(lamp.RetryAt);
                    return;
                }
                LampArrayLayout layout;
                string rejection;
                try { layout = _descriptors.Read(lamp.Path, out rejection); }
                catch
                {
                    layout = null;
                    rejection = null;
                }
                if (layout == null)
                {
                    if (rejection != null) Reject(lamp, rejection, ref pass);
                    else Miss(lamp, "its descriptor could not be read", ref pass);
                    return;
                }
                lamp.Layout = layout;
                lamp.Stage = 1;
                lamp.Attempt = 0;
                lamp.NextAt = pass.Now;
            }

            while (lamp.Stage != 0)
            {
                if (run.Stop) return;
                if (pass.Now < lamp.NextAt)
                {
                    pass.DueAt(lamp.NextAt);
                    return;
                }
                lamp.ProbeIo ??= Open(lamp.Stage == 1 ? _openQuery : _openClaim, lamp.Path);
                byte[] report = null;
                bool answered = false;
                if (lamp.ProbeIo != null)
                {
                    report = new byte[Math.Max(lamp.Layout.FeatureLength, LampArrayProtocol.AttributesLength)];
                    answered = Get(lamp.ProbeIo, lamp.Layout.AttributesId, report);
                }
                if (answered)
                {
                    EndProbe(lamp);
                    // A worker told to stop while the read was under way
                    // asks nothing more, the device's name included.
                    if (!run.Stop) Described(lamp, report, ref pass);
                    return;
                }
                // A handle that could not be opened is not tried again: the
                // other handle is, or the backoff.
                if (lamp.ProbeIo != null && ++lamp.Attempt < ReadAttempts)
                {
                    lamp.NextAt = _ticks() + ReadRetryStepMs * lamp.Attempt;
                    pass.DueAt(lamp.NextAt);
                    return;
                }
                Dispose(ref lamp.ProbeIo);
                if (lamp.Stage == 1)
                {
                    lamp.Stage = 2;
                    lamp.Attempt = 0;
                    lamp.NextAt = pass.Now;
                    continue;
                }
                EndProbe(lamp);
                Miss(lamp, "it did not answer its attributes read", ref pass);
            }
        }

        /// <summary>The attributes report came in: the unit is described,
        /// or turned down for what the report says.</summary>
        private void Described(Lamp lamp, byte[] report, ref Pass pass)
        {
            var layout = lamp.Layout;
            if (!LampArrayProtocol.TryParseAttributes(report, out var attributes))
            {
                Reject(lamp, "its attributes report is shorter than " + LampArrayProtocol.AttributesLength + " bytes", ref pass);
                return;
            }
            bool? agree;
            try { agree = _descriptors.AttributesAgree(lamp.Path, layout, report, attributes); }
            catch { agree = null; }
            if (agree == null)
            {
                Miss(lamp, "its descriptor could not be read", ref pass);
                return;
            }
            if (agree == false)
            {
                Reject(lamp, "Windows reads its attributes report otherwise than the LampArray layout does: "
                             + LampArrayProtocol.Hex(report, LampArrayProtocol.AttributesLength), ref pass);
                return;
            }
            if (attributes.LampCount <= 0 || attributes.LampCount > LampArrayProtocol.MaxLamps)
            {
                Reject(lamp, $"it declares {attributes.LampCount} lamps", ref pass);
                return;
            }

            bool woke = lamp.Unit != null;
            string name = lamp.Unit?.Name;
            if (name == null)
            {
                try { name = _descriptors.ProductName(lamp.Path); }
                catch { name = null; }
            }
            Guid container = lamp.Unit?.ContainerId ?? Guid.Empty;
            if (container == Guid.Empty) container = ContainerOf(lamp.Path);
            bool lit = LitNow(lamp.Path);
            lamp.Unit = new LampArrayUnit(lamp.Path, lamp.VendorId, lamp.ProductId)
            {
                ContainerId = container,
                Name = name,
                Kind = attributes.Kind > int.MaxValue ? int.MaxValue : (int)attributes.Kind,
                LampCount = attributes.LampCount,
                MinUpdateIntervalUs = attributes.MinUpdateIntervalUs,
                AttributesId = layout.AttributesId,
                RequestId = layout.RequestId,
                ResponseId = layout.ResponseId,
                RangeId = layout.RangeId,
                ControlId = layout.ControlId,
                HasControl = layout.HasControl,
                FeatureLength = layout.FeatureLength,
                MultiUpdateSlots = layout.MultiUpdateSlots,
                MultiId = layout.MultiId,
                InputBindingBits = layout.InputBindingBits,
                DescribesLamps = layout.DescribesLamps,
                WindowsLit = lit,
            };
            lamp.WindowsLit = lit;
            lamp.Asleep = false;
            lamp.MissNamed = false;
            lamp.LampsRead = false;
            pass.Units = true;
            PadForge.Engine.SdlDiagLog.WriteLine(woke
                ? $"PERIPHERAL LampArray {lamp.Label} answers again"
                : $"PERIPHERAL LampArray found {lamp.Label} {lamp.VendorId:X4}:{lamp.ProductId:X4} lamps={attributes.LampCount}"
                  + $" kind={attributes.Kind} interval={attributes.MinUpdateIntervalUs}us"
                  + $" ids={layout.AttributesId:X2}/{layout.RequestId:X2}/{layout.ResponseId:X2}/{layout.RangeId:X2}/"
                  + (layout.HasControl ? layout.ControlId.ToString("X2") : "--")
                  + $" windowsLit={lit}");
        }

        /// <summary>The collection is not one PadForge drives. A new one is
        /// named once and left alone while it stays. A unit that was listed
        /// and no longer passes stays listed asleep, and is asked again.</summary>
        private void Reject(Lamp lamp, string why, ref Pass pass)
        {
            if (lamp.Unit != null)
            {
                Miss(lamp, why, ref pass);
                return;
            }
            EndProbe(lamp);
            lamp.Rejected = true;
            PadForge.Engine.SdlDiagLog.WriteLine(
                $"PERIPHERAL LampArray {lamp.VendorId:X4}:{lamp.ProductId:X4} not used: {why}");
        }

        /// <summary>The collection did not answer: it is asked again after
        /// the backoff, 5 seconds, then 10, then 15 at most. Named once
        /// until it answers.</summary>
        private void Miss(Lamp lamp, string why, ref Pass pass)
        {
            EndProbe(lamp);
            lamp.RetryAt = pass.Now + Backoff(lamp);
            pass.DueAt(lamp.RetryAt);
            if (lamp.MissNamed) return;
            lamp.MissNamed = true;
            PadForge.Engine.SdlDiagLog.WriteLine(
                $"PERIPHERAL LampArray {lamp.Label} is asked again later: {why}");
        }

        /// <summary>The wait before the next try, counted up.</summary>
        private static long Backoff(Lamp lamp)
            => Math.Min((long)FirstRetryMs << Math.Min(lamp.Misses++, 4), MaxRetryMs);

        private static void EndProbe(Lamp lamp)
        {
            Dispose(ref lamp.ProbeIo);
            lamp.Stage = 0;
            lamp.Attempt = 0;
        }

        /// <summary>Reads Windows' lighting switch for every listed unit
        /// and lists a change.</summary>
        private void ReadWindowsLit(Dictionary<string, Lamp> lamps, ref Pass pass)
        {
            foreach (var lamp in lamps.Values)
            {
                if (lamp.Unit == null) continue;
                SetWindowsLit(lamp, LitNow(lamp.Path), ref pass);
            }
        }

        private static void SetWindowsLit(Lamp lamp, bool lit, ref Pass pass)
        {
            if (lit == lamp.WindowsLit) return;
            lamp.WindowsLit = lit;
            lamp.Unit = lamp.Unit with { WindowsLit = lit };
            pass.Units = true;
            PadForge.Engine.SdlDiagLog.WriteLine(lit
                ? $"PERIPHERAL LampArray {lamp.Label} is lit by Windows Dynamic Lighting"
                : $"PERIPHERAL LampArray {lamp.Label} is no longer lit by Windows Dynamic Lighting");
        }

        /// <summary>Whether Windows lights a collection now. A read that
        /// cannot be made counts as lit: standing down for nothing costs a
        /// color, and writing beside Windows makes two writers fight.</summary>
        private bool LitNow(string path)
        {
            try { return _windowsLit(path); }
            catch { return true; }
        }

        private Guid ContainerOf(string path)
        {
            try { return _containerOf(path); }
            catch { return Guid.Empty; }
        }

        // ─────────────────────────────────────────────
        //  The claim
        // ─────────────────────────────────────────────

        /// <summary>One unit's step of the pass: claim it, read its lamps,
        /// paint it, hold it or hand it back, by what its path resolves to
        /// now.</summary>
        private void Paint(WorkerRun run, Lamp lamp, LinkTable links, ref Pass pass)
        {
            var unit = lamp.Unit;
            var path = new OutputPath(OutputFamily.LampArrayUnit, unit.Path);
            int rgb = 0;
            PeripheralOutputs.Claimant ruler = default;
            bool wanted = links.ByPath.ContainsKey(path) && PeripheralOutputs.TryResolveColor(path, out rgb, out ruler);

            if (lamp.WindowsLit)
            {
                // Windows holds the device. A claim held until now is let
                // go without a frame: a hand-back would switch Windows' own
                // effect off. A hand-back still owed is dropped for the
                // same reason.
                if (lamp.Owed)
                {
                    lamp.Owed = false;
                    pass.Claims = true;
                }
                if (lamp.Phase == Phase.Idle) return;
                if (lamp.Claimed)
                    PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL LampArray {lamp.Label} lighting left to Windows Dynamic Lighting");
                Release(lamp, ref pass);
                return;
            }

            if (!wanted)
            {
                if (lamp.Phase == Phase.Idle)
                {
                    // Nothing claims it and it answers again: the hand-back
                    // it has been owed since it fell asleep. A stopping
                    // worker leaves that to its finally.
                    if (lamp.Owed && !run.Stop) PayBack(lamp, ref pass);
                    return;
                }
                if (lamp.Claimed && !HandBack(lamp, ref pass)) return;
                Release(lamp, ref pass);
                return;
            }

            // A stopping worker claims nothing more and paints nothing
            // more: its finally hands back what it holds.
            if (run.Stop) return;

            if (lamp.Phase == Phase.Idle)
            {
                lamp.Io = Open(_openClaim, unit.Path);
                if (lamp.Io == null)
                {
                    Sleep(lamp, "it could not be opened", ref pass);
                    return;
                }
                if (!lamp.LampsRead)
                {
                    // Always the request for lamp 0 first: a device counts
                    // on from the lamp it last answered for, and one
                    // firmware stops at its last lamp and never starts over
                    // (RP2040MacropadHidSample src/Lighting/LampArray.c:43).
                    if (!Set(lamp, LampArrayProtocol.AttributesRequest(unit.RequestId, 0)))
                    {
                        Sleep(lamp, "its lamp request failed", ref pass);
                        return;
                    }
                    lamp.Descriptions = new List<LampDescription>(unit.LampCount);
                    lamp.LampAttempt = 0;
                    lamp.Rerequested = false;
                    lamp.LampNextAt = pass.Now;
                }
                lamp.Phase = Phase.Reading;
            }

            if (lamp.Phase == Phase.Reading)
            {
                if (!lamp.LampsRead && !ReadLamps(run, lamp, ref pass)) return;
                if (run.Stop) return;
                unit = lamp.Unit;
                // The crash path learns of a claim before the claim goes
                // out, so no moment exists in which the device is claimed
                // and the hand-back does not know it (HidppBackend.PaintLighting).
                NoteClaimed(unit);
                pass.Claims = true;
                if (unit.HasControl && !Set(lamp, LampArrayProtocol.Control(unit.ControlId, autonomous: false)))
                {
                    // The device may have taken the report that failed.
                    Sleep(lamp, "its claim failed", ref pass, claimSent: true);
                    return;
                }
                lamp.ClaimedAt = _ticks();
                lamp.SettleUntil = lamp.ClaimedAt + unit.SettleMs;
                lamp.Sent = false;
                lamp.Phase = Phase.Settling;
                // This claim's own end hands the device back from here on.
                lamp.Owed = false;
                PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL LampArray {lamp.Label} lighting claimed");
            }

            if (lamp.Phase == Phase.Settling)
            {
                // The first color follows the claim after the device's own
                // interval and no later: on one firmware the claim alone
                // turns the array off until a color arrives
                // (acer-predator-helios-rgb protocol/PROTOCOL.md:215).
                if (pass.Now < lamp.SettleUntil)
                {
                    pass.DueAt(lamp.SettleUntil);
                    return;
                }
                if (!Paint(lamp, rgb, ruler.Keys, full: true, ref pass)) return;
                lamp.Phase = Phase.Lit;
                lamp.Misses = 0;
                pass.DueAt(lamp.ClaimedAt + ReassertMs);
                return;
            }

            // Lit. An update is sent no sooner than the device's interval
            // after the last one, the re-assert's color included: a device
            // may ignore a report that comes sooner (HID Usage Tables 1.6,
            // 26.7). The claim keeps a clock of its own, so a color that
            // changes faster than the re-assert never holds it back for
            // longer than one interval.
            long spaced = lamp.SentAt + unit.PaintIntervalMs;
            bool reassert = pass.Now - lamp.ClaimedAt >= ReassertMs;
            if (!reassert && Showing(lamp, rgb, ruler.Keys))
            {
                pass.DueAt(lamp.ClaimedAt + ReassertMs);
                return;
            }
            if (pass.Now < spaced)
            {
                pass.DueAt(spaced);
                return;
            }
            if (reassert)
            {
                if (unit.HasControl && !Set(lamp, LampArrayProtocol.Control(unit.ControlId, autonomous: false)))
                {
                    Sleep(lamp, "its claim failed", ref pass);
                    return;
                }
                lamp.ClaimedAt = _ticks();
            }
            if (!Paint(lamp, rgb, ruler.Keys, full: reassert, ref pass)) return;
            // Lamps a range-only device could not take in one update go out
            // after the next interval.
            pass.DueAt(lamp.Shown != null && !Showing(lamp, rgb, ruler.Keys)
                ? lamp.SentAt + unit.PaintIntervalMs
                : lamp.ClaimedAt + ReassertMs);
        }

        /// <summary>The color of every lamp for a claim (#557): its key
        /// frame rendered over its color, or null where the unit is painted
        /// one color: no frame, no light map, or levels it could not read.</summary>
        private static int[] LampColors(LampArrayUnit unit, int rgb, KeyFrame keys)
        {
            if (keys == null || keys.IsEmpty || unit.Map == null || !unit.Map.PerLight || unit.LampLevels == null
                || unit.Map.Lights.Count != unit.LampLevels.Count)
                return null;
            return LightRenderer.Render(unit.Map, rgb, keys);
        }

        /// <summary>Whether the unit shows what the claim asks for now.</summary>
        private static bool Showing(Lamp lamp, int rgb, KeyFrame keys)
        {
            var colors = LampColors(lamp.Unit, rgb, keys);
            return colors == null
                ? lamp.Shown == null && rgb == lamp.SentRgb
                : LightRenderer.Same(colors, lamp.Shown);
        }

        /// <summary>Paints the claim: one color the way <see cref="Send"/>
        /// does, or lamp by lamp where the claim carries a key frame the
        /// unit's map can show (#557). <paramref name="full"/> sends every
        /// lamp, as the first paint and a re-assert do. Otherwise only the
        /// lamps whose color changed go out. False when a write failed, and
        /// the unit is asleep then.</summary>
        private bool Paint(Lamp lamp, int rgb, KeyFrame keys, bool full, ref Pass pass)
        {
            var colors = LampColors(lamp.Unit, rgb, keys);
            if (colors == null)
            {
                if (!Send(lamp, rgb, ref pass)) return false;
                lamp.Shown = null;
                return true;
            }
            var shown = lamp.Shown;
            var due = new List<int>(colors.Length);
            for (int i = 0; i < colors.Length; i++)
                if (full || shown == null || shown[i] != colors[i]) due.Add(i);
            if (due.Count == 0) return true;
            var frames = LampArrayProtocol.LampFrames(lamp.Unit, colors, due, out int covered);
            foreach (var frame in frames)
            {
                if (Set(lamp, frame)) continue;
                Sleep(lamp, "a color write failed", ref pass);
                return false;
            }
            // Lamps the update did not reach keep what they showed. From a
            // one-color paint that is the color sent then.
            if (shown == null)
            {
                shown = new int[colors.Length];
                Array.Fill(shown, lamp.Sent ? lamp.SentRgb : -1);
            }
            else shown = (int[])shown.Clone();
            for (int i = 0; i < covered; i++) shown[due[i]] = colors[due[i]];
            lamp.Shown = shown;
            lamp.SentRgb = -1;
            lamp.SentAt = _ticks();
            lamp.Sent = true;
            return true;
        }


        /// <summary>
        /// Reads the lamps of a unit that is open, from where the last pass
        /// left off, <see cref="LampsPerPass"/> at most. True when the read
        /// is over, with the unit's runs built or with the unit marked for
        /// a blind paint. False while lamps are left for the next pass,
        /// while a pause runs, when the worker is stopping, and when a
        /// write failed, in which case the unit is asleep.
        ///
        /// <para>The device counts on by itself after each response, so one
        /// request is followed by one read per lamp (HID Usage Tables 1.6,
        /// 26.8.2, and OpenRGB Controllers/HIDLampArrayController/HIDLampArrayController.cpp:160-173).
        /// "The Host must always check the LampId of the returned report":
        /// a response that names another lamp is asked for by name and read
        /// again, which also serves firmware that does not count on, the
        /// way one host asks before every read (mistel-lights
        /// Sources/BaroccoHID/LampArrayClient.swift:116-127). A second
        /// wrong answer for the same lamp, or a read that fails
        /// <see cref="ReadAttempts"/> times, ends the read with no levels,
        /// and the unit is painted blind.</para>
        /// </summary>
        private bool ReadLamps(WorkerRun run, Lamp lamp, ref Pass pass)
        {
            var unit = lamp.Unit;
            if (pass.Now < lamp.LampNextAt)
            {
                pass.DueAt(lamp.LampNextAt);
                return false;
            }
            var report = new byte[Math.Max(unit.FeatureLength, LampArrayProtocol.ResponseLength)];
            int read = 0;
            while (lamp.Descriptions.Count < unit.LampCount)
            {
                if (run.Stop) return false;
                if (read == LampsPerPass)
                {
                    // Enough for one pass. The other units have their turn,
                    // and the read goes on at the next.
                    pass.DueAt(pass.Now);
                    return false;
                }
                int due = lamp.Descriptions.Count;
                if (!Get(lamp.Io, unit.ResponseId, report))
                {
                    if (++lamp.LampAttempt >= ReadAttempts)
                    {
                        Blind(lamp, $"lamp {due} did not answer");
                        return true;
                    }
                    lamp.LampNextAt = _ticks() + ReadRetryStepMs * lamp.LampAttempt;
                    pass.DueAt(lamp.LampNextAt);
                    return false;
                }
                lamp.LampAttempt = 0;
                if (!LampArrayProtocol.TryParseLampDescription(report, unit.InputBindingBits, out var described))
                {
                    Blind(lamp, "its lamp response is shorter than " + LampArrayProtocol.ResponseLength + " bytes");
                    return true;
                }
                int id = described.LampId;
                if (id != due)
                {
                    if (lamp.Rerequested)
                    {
                        Blind(lamp, $"lamp {due} answered as lamp {id} twice");
                        return true;
                    }
                    lamp.Rerequested = true;
                    if (!Set(lamp, LampArrayProtocol.AttributesRequest(unit.RequestId, due)))
                    {
                        Sleep(lamp, "its lamp request failed", ref pass);
                        return false;
                    }
                    continue;
                }
                lamp.Rerequested = false;
                lamp.Descriptions.Add(described);
                read++;
            }
            var levels = lamp.Descriptions.Select(d => d.Levels).ToArray();
            var runs = LampArrayProtocol.BuildRuns(levels, unit.MultiUpdateSlots);
            // The light map (#557) only where the lamp response holds the
            // descriptions where PadForge reads them, with the names Map Key
            // Lights learned for the model.
            var fromLamps = unit.DescribesLamps ? LampArrayProtocol.LightMapOf(lamp.Descriptions, unit.Kind) : null;
            var deviceMap = fromLamps == null ? null : new LightMap(fromLamps.Lights, fromLamps.Source) { Product = unit.Product };
            var map = LearnedKeyMaps.Apply(deviceMap);
            lamp.Unit = unit with { Runs = runs, LampLevels = levels, Map = map, DeviceMap = deviceMap };
            lamp.Descriptions = null;
            lamp.LampsRead = true;
            if (map != null) PeripheralLightMaps.Publish(new OutputPath(OutputFamily.LampArrayUnit, unit.Path), map);
            // The level counts decide what a color looks like on the
            // device, so the log holds them for a color that comes out dim.
            PadForge.Engine.SdlDiagLog.WriteLine(
                $"PERIPHERAL LampArray {lamp.Label} lamps read: {unit.LampCount}, painted as {LampArrayProtocol.DescribeRuns(runs)}");
            return true;
        }

        /// <summary>The lamps could not be read. The unit is painted with
        /// the frame that needs no level count, and is not asked again
        /// while it stays awake.</summary>
        private static void Blind(Lamp lamp, string why)
        {
            lamp.Unit = lamp.Unit with { Runs = null, LampLevels = null, Map = null, DeviceMap = null };
            lamp.Descriptions = null;
            lamp.LampsRead = true;
            PadForge.Engine.SdlDiagLog.WriteLine(
                $"PERIPHERAL LampArray {lamp.Label} lamps could not be read ({why}), painting without their level counts");
        }

        /// <summary>Sends one color. False when a write failed, and the
        /// unit is asleep then.</summary>
        private bool Send(Lamp lamp, int rgb, ref Pass pass)
        {
            foreach (var frame in LampArrayProtocol.Frames(lamp.Unit, rgb))
            {
                if (Set(lamp, frame)) continue;
                Sleep(lamp, "a color write failed", ref pass);
                return false;
            }
            lamp.SentRgb = rgb;
            lamp.SentAt = _ticks();
            lamp.Sent = true;
            return true;
        }

        /// <summary>
        /// Gives a claimed unit back to its own effect, when nothing claims
        /// it any more. Windows' switch is read again first, since the
        /// cached answer can be a second old: a unit Windows now lights is
        /// let go without a frame. A device with a control report gets
        /// AutonomousMode on. One without has its lamps turned off, which
        /// is an update like any other and waits out the interval since
        /// the last one. False while that wait runs, when the unit stays
        /// claimed.
        /// </summary>
        private bool HandBack(Lamp lamp, ref Pass pass)
        {
            var unit = lamp.Unit;
            if (LitNow(unit.Path))
            {
                SetWindowsLit(lamp, true, ref pass);
                PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL LampArray {lamp.Label} lighting left to Windows Dynamic Lighting");
                return true;
            }
            bool sent;
            if (unit.HasControl)
            {
                sent = Set(lamp, LampArrayProtocol.Control(unit.ControlId, autonomous: true));
            }
            else if (!lamp.Sent)
            {
                // Nothing was sent to it, so nothing is on it to undo.
                return true;
            }
            else
            {
                long spaced = lamp.SentAt + unit.PaintIntervalMs;
                if (pass.Now < spaced)
                {
                    pass.DueAt(spaced);
                    return false;
                }
                sent = Set(lamp, LampArrayProtocol.AllOff(unit));
            }
            if (sent)
            {
                PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL LampArray {lamp.Label} lighting handed back");
                return true;
            }
            Sleep(lamp, "its hand-back failed", ref pass);
            return false;
        }

        /// <summary>
        /// Pays the hand-back a unit is owed (<see cref="Lamp.Owed"/>), once
        /// it answers again and nothing claims it: the report
        /// <see cref="HandBack"/> sends, on a channel opened for it and
        /// closed after it. A device that reset meanwhile has the mode back
        /// on, and enabling it again "is a no-op" (HID Usage Tables 1.6,
        /// 26.10.1). The unit slept through its backoff, 5 seconds at the
        /// least, so the update interval since its last color is long
        /// over. A unit Windows lights by now is owed nothing. A report
        /// that fails puts the unit back to sleep with the debt standing.
        /// </summary>
        private void PayBack(Lamp lamp, ref Pass pass)
        {
            var unit = lamp.Unit;
            if (LitNow(unit.Path))
            {
                SetWindowsLit(lamp, true, ref pass);
                lamp.Owed = false;
                pass.Claims = true;
                return;
            }
            lamp.Io = Open(_openClaim, unit.Path);
            if (lamp.Io == null)
            {
                Sleep(lamp, "it could not be opened", ref pass);
                return;
            }
            if (!Set(lamp, HandBackFrame(unit)))
            {
                Sleep(lamp, "its hand-back failed", ref pass);
                return;
            }
            Dispose(ref lamp.Io);
            lamp.Owed = false;
            lamp.Misses = 0;
            pass.Claims = true;
            PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL LampArray {lamp.Label} lighting handed back");
        }

        /// <summary>The stop path's hand-back, on the worker's own channel.
        /// A unit Windows lights gets nothing. A unit that is asleep gets
        /// nothing either, unless its hand-back is owed
        /// (<see cref="Lamp.Owed"/>). That one goes out through a handle
        /// of its own, bounded, as on the crash path, since the worker has
        /// no channel to the unit.</summary>
        private void HandBackOnStop(Lamp lamp)
        {
            var unit = lamp.Unit;
            if (lamp.Owed && unit != null)
            {
                if (lamp.WindowsLit || LitNow(unit.Path)) return;
                try { _setFeatureOnce(unit.Path, HandBackFrame(unit), OneShotMs); } catch { }
                return;
            }
            if (!lamp.Claimed || lamp.Io == null || lamp.Asleep) return;
            if (lamp.WindowsLit || LitNow(unit.Path)) return;
            if (unit.HasControl)
            {
                Set(lamp, LampArrayProtocol.Control(unit.ControlId, autonomous: true));
                return;
            }
            if (!lamp.Sent) return;
            // The lamps' off is an update, so it waits out what is left of
            // the interval since the last one, which a device may hold a
            // host to. The wait is the interval at most.
            long left = lamp.SentAt + unit.PaintIntervalMs - _ticks();
            if (left > 0) _pause((int)Math.Min(left, LampArrayProtocol.MaxIntervalMs));
            Set(lamp, LampArrayProtocol.AllOff(unit));
        }

        /// <summary>Closes a unit's channel and forgets its claim. What was
        /// learned of its lamps is kept: they are read once per arrival.</summary>
        private static void Release(Lamp lamp, ref Pass pass)
        {
            if (lamp.Claimed) pass.Claims = true;
            Dispose(ref lamp.Io);
            lamp.Phase = Phase.Idle;
            lamp.Descriptions = null;
            lamp.Shown = null;
            lamp.Sent = false;
        }

        /// <summary>
        /// A write failed, or the unit could not be opened: the contract's
        /// rule for a unit that cannot be written to now. Its channel is
        /// closed and its claim ended, with nothing sent, since there is
        /// no channel to send it on. A unit that was claimed, or whose
        /// claim report is the one that failed
        /// (<paramref name="claimSent"/>), is owed its hand-back from here
        /// on (<see cref="Lamp.Owed"/>): the device may have missed that
        /// one report and kept the claim. What was read of its lamps is
        /// forgotten. It is listed asleep, a walk is asked for, since the
        /// device may have left, and it is described again after the
        /// backoff.
        /// </summary>
        private void Sleep(Lamp lamp, string why, ref Pass pass, bool claimSent = false)
        {
            if (lamp.Claimed || claimSent)
            {
                lamp.Owed = true;
                pass.Claims = true;
            }
            Release(lamp, ref pass);
            lamp.Asleep = true;
            lamp.LampsRead = false;
            lamp.Unit = lamp.Unit with { Runs = null };
            EndProbe(lamp);
            lamp.RetryAt = pass.Now + Backoff(lamp);
            pass.DueAt(lamp.RetryAt);
            pass.Units = true;
            pass.Failed = true;
            PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL LampArray {lamp.Label} is asleep: {why}");
        }

        private static IHidReportChannel Open(Func<string, IHidReportChannel> open, string path)
        {
            try { return open(path); }
            catch { return null; }
        }

        private static bool Set(Lamp lamp, byte[] frame)
        {
            try { return lamp.Io != null && lamp.Io.SetFeature(frame); }
            catch { return false; }
        }

        private static bool Get(IHidReportChannel io, byte reportId, byte[] buffer)
        {
            try { return io != null && io.GetFeature(reportId, buffer); }
            catch { return false; }
        }

        private static void Dispose(ref IHidReportChannel io)
        {
            var channel = io;
            io = null;
            if (channel == null) return;
            try { channel.Dispose(); } catch { }
        }

        // ─────────────────────────────────────────────
        //  What the host and the crash path read
        // ─────────────────────────────────────────────

        /// <summary>The report that hands a unit back: AutonomousMode on,
        /// or every lamp off for a device with no control report.</summary>
        private static byte[] HandBackFrame(LampArrayUnit unit)
            => unit.HasControl
                ? LampArrayProtocol.Control(unit.ControlId, autonomous: true)
                : LampArrayProtocol.AllOff(unit);

        /// <summary>Adds one unit to the crash path's record ahead of its
        /// claim. Worker thread only, swapped whole.</summary>
        private void NoteClaimed(LampArrayUnit unit)
        {
            var current = Volatile.Read(ref _claimed);
            foreach (var entry in current)
                if (string.Equals(entry.Path, unit.Path, StringComparison.OrdinalIgnoreCase)) return;
            var next = new (string, byte[])[current.Length + 1];
            current.CopyTo(next, 0);
            next[current.Length] = (unit.Path, HandBackFrame(unit));
            Volatile.Write(ref _claimed, next);
        }

        /// <summary>Records which units hold a claim or are owed a
        /// hand-back, for the crash path.</summary>
        private void PublishClaimed(WorkerRun run, Dictionary<string, Lamp> lamps)
        {
            if (!InCharge(run)) return;
            var claimed = new List<(string, byte[])>();
            foreach (var lamp in lamps.Values)
                if (lamp.Claimed || lamp.Owed) claimed.Add((lamp.Path, HandBackFrame(lamp.Unit)));
            Volatile.Write(ref _claimed, claimed.Count == 0 ? NoClaims : claimed.ToArray());
        }

        /// <summary>One new snapshot of every listed unit, in the order of
        /// their paths so two snapshots of the same units read alike, then
        /// the notice.</summary>
        private void Publish(WorkerRun run, Dictionary<string, Lamp> lamps, bool scanned)
        {
            if (!InCharge(run)) return;
            var units = new List<DirectUnit>();
            foreach (var lamp in lamps.Values)
            {
                if (lamp.Unit == null) continue;
                units.Add(new DirectUnit(OutputFamily.LampArrayUnit, lamp.Unit.Path, lamp.Unit.ContainerId,
                    lamp.Unit.VendorId, lamp.Unit.ProductId)
                {
                    Name = lamp.Unit.Name,
                    Kind = lamp.Unit.Kind,
                    WindowsLit = lamp.WindowsLit,
                    Asleep = lamp.Asleep,
                });
            }
            units.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase));
            Volatile.Write(ref _snapshot, new DirectBackendSnapshot(units.ToArray(), scanned));
            try { SnapshotChanged?.Invoke(); } catch { }
        }
    }
}
