using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Engine.Data;

namespace PadForge.Common.Recording
{
    /// <summary>A device's comparison while a recording runs: its plan and
    /// the Raw Input collection it reads, or why it has no second reader.</summary>
    internal sealed class ReaderLayout
    {
        public ReaderPlan Plan;
        public string Reason;
        public bool Hidden;
        public IntPtr Handle;
        public byte[] ReportIds = Array.Empty<byte>();
        public int ReportLength;

        internal IntPtr Preparsed;
        internal Dictionary<ushort, int> ControlOfDataIndex;
        internal byte[] Report;
        internal byte[] DataList;
        internal int MaxData;
    }

    /// <summary>The newest report values of a layout's collection: by
    /// control, a button 1 when down, whether its report came, when the
    /// newest report naming a compared control came and the newest naming
    /// none, and how many naming none have come.</summary>
    internal sealed class ReaderReading
    {
        public uint[] Values = Array.Empty<uint>();
        public bool[] Known = Array.Empty<bool>();
        public long NamedTicks, OtherTicks;
        public int OtherCount;
    }

    /// <summary>What the comparison asks Windows, behind one seam so a test
    /// feeds its own readers.</summary>
    internal interface IReaderSource
    {
        /// <summary>A device's layout, null while it is being made.</summary>
        ReaderLayout Layout(UserDevice device);

        /// <summary>The newest reports of a layout's collection, parsed into
        /// <paramref name="into"/>. False when no report of it came.</summary>
        bool Read(ReaderLayout layout, ReaderReading into);

        /// <summary>Keeps Raw Input's copy of these layouts' collections, and
        /// of no other.</summary>
        void Watch(IReadOnlyList<ReaderLayout> layouts);

        /// <summary>Lets a layout go, and what it holds.</summary>
        void Release(ReaderLayout layout);

        /// <summary>Lets every layout go, the ones never handed out too.</summary>
        void ReleaseAll();
    }

    /// <summary>
    /// The device half of the reader comparison (#600 item 32): which Raw
    /// Input collection belongs to a device, its controls and its parsed
    /// reports, DirectInput's own objects of it, and the fact a device's
    /// header line carries for its second reader.
    ///
    /// <para>A DirectInput object read creates the device object and lists
    /// its objects, never acquiring it, as SDL's own enumeration does on
    /// every game controller (SDL_dinputjoystick.c:466-469). DirectInput can
    /// take long on some PCs (962-964), so a layout is made on a thread of
    /// its own and the recorder waits for nothing. The test host asks
    /// Windows nothing: with <see cref="OffLimitsForTest"/> set, a layout
    /// comes from <see cref="Seam"/> or not at all.</para>
    /// </summary>
    internal static class RecorderReaders
    {
        /// <summary>Set by the test assembly for the life of its process,
        /// never by the app.</summary>
        internal static bool OffLimitsForTest;

        /// <summary>Test seam: the readers a test feeds.</summary>
        internal static IReaderSource Seam;

        /// <summary>Test seam: the Raw Input collection at a path and its
        /// usage, in place of <see cref="FindCollection"/>, for a layout
        /// <see cref="WindowsReaders.Make"/> makes.</summary>
        internal static Func<string, (IntPtr Handle, ushort Usage)> CollectionSeam;

        /// <summary>Test seam: SDL's mapping text for a joystick, in place of
        /// the engine's answer, for <see cref="WindowsReaders.Make"/>.</summary>
        internal static Func<uint, string> MappingSeam;

        private static readonly WindowsReaders Windows = new();

        /// <summary>The readers the comparison asks.</summary>
        internal static IReaderSource Source => Seam ?? Windows;

        // ─────────────────────────────────────────────
        //  The header fact
        // ─────────────────────────────────────────────

        /// <summary>A device's second reader as its header line says it,
        /// for a device at no XInput place (that one's xinput fact names its
        /// place): rawinput, or none and why.</summary>
        internal static string SecondFact(UserDevice device)
        {
            if (device == null) return null;
            // A peer's device is read on the peer's PC, whatever its GUID or
            // its type.
            if (device.Device is PadForge.Engine.RemoteLink.RemotePeerDevice) return "none, it is read on another PC";
            // A keyboard, mouse or touchpad Windows holds: one Raw Input's
            // keyboard and mouse readers hand over, or one saved and not
            // connected now. A program's or a replay's keyboard is
            // PadForge's own reader, and no Windows driver holds it.
            if ((device.IsKeyboard || device.IsMouse || device.IsTouchpad) && device.Device is null or SdlKeyboardWrapper or SdlMouseWrapper)
                return "none, Windows opens it for itself alone";
            // An SDL device's GUID, from its open wrapper or as it was saved.
            // PadForge's own readers give none.
            string guid = device.Device != null ? device.Device.SdlGuid : device.SdlGuid;
            if (RecorderReaderPlan.Signature(guid) < 0) return "none, PadForge reads it itself";
            var (cls, reason) = RecorderReaderPlan.Classify(guid, false, IsSpaceMouse(device, guid), device.VendorId, device.ProdId,
                RecordingKinds.Of(device) == PadForge.Engine.Recording.RecordingControlKind.Gamepad);
            if (cls == ReaderClass.None) return "none, " + reason;
            // The rule Make applies, so the header and the reader line agree.
            if (MergeReason(device.VendorId, device.ProdId, device.Device) is { } merged) return "none, " + merged;
            return "rawinput";
        }

        /// <summary>
        /// Why a Bliss-Box port has no second reader, or null for any other
        /// device and for a port nothing merges into: while Read Bliss-Box
        /// Adapters is on, PadForge merges the adapter's own state into the
        /// reading of a port's row SDL reads raw (BlissBoxRuntime.Merge, which
        /// takes no row SDL opened as a gamepad, BlissBoxRuntime.OpenedRaw),
        /// so that reading is not the reports Raw Input hands over.
        /// </summary>
        internal static string MergeReason(ushort vendor, ushort product, ISdlInputDevice live)
            => PadForge.Engine.Common.BlissBox.BlissBoxApi.ReadsRaw(vendor, product)
               && live is SdlDeviceWrapper wrapper && wrapper.GamepadHandle == IntPtr.Zero
                ? "PadForge merges its adapter's own state into its reading"
                : null;

        /// <summary>Whether a device is PadForge's SpaceMouse reader's
        /// virtual joystick: SDL's virtual signature, a 3Dconnexion or
        /// Logitech vendor (SpaceMouseService.cs:46-67) and a device path,
        /// which the service gives its pucks.</summary>
        internal static bool IsSpaceMouse(UserDevice device, string sdlGuid)
            => RecorderReaderPlan.Signature(sdlGuid) == 'v' && device.VendorId is 0x256F or 0x046D
               && !string.IsNullOrEmpty(device.DevicePath) && device.DevicePath.StartsWith(@"\\?\", StringComparison.Ordinal);

        // ─────────────────────────────────────────────
        //  Raw Input's collections
        // ─────────────────────────────────────────────

        private const uint RimTypeHid = 2;
        private const uint RidiDeviceName = 0x20000007, RidiDeviceInfo = 0x2000000b, RidiPreparsedData = 0x20000005;
        private const int HidPInput = 0;
        internal const int HidPSuccess = 0x00110000;

        [StructLayout(LayoutKind.Sequential)]
        private struct RawDeviceListEntry
        {
            public IntPtr Handle;
            public uint Type;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetRawInputDeviceList([Out] RawDeviceListEntry[] list, ref uint count, uint size);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetRawInputDeviceInfoW(IntPtr device, uint command, IntPtr data, ref uint size);

        [DllImport("hid.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int HidP_GetCaps(IntPtr preparsed, [Out] byte[] caps);

        [DllImport("hid.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int HidP_GetButtonCaps(int reportType, [Out] byte[] caps, ref ushort length, IntPtr preparsed);

        [DllImport("hid.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int HidP_GetValueCaps(int reportType, [Out] byte[] caps, ref ushort length, IntPtr preparsed);

        [DllImport("hid.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern uint HidP_MaxDataListLength(int reportType, IntPtr preparsed);

        [DllImport("hid.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int HidP_GetData(int reportType, [In, Out] byte[] dataList, ref uint dataLength, IntPtr preparsed,
            [In] byte[] report, uint reportLength);

        /// <summary>
        /// The Raw Input handle of the game controller collection at a path,
        /// as Raw Input names it (RIDI_DEVICENAME, case ignored, as
        /// RelativeHidAxes's DeviceName pairs one), with its usage, vendor and
        /// product, or zero. Only a joystick, game pad or multi-axis
        /// controller, the collections PadForge's listener holds
        /// (the joystick, game pad and multi-axis entries RawInputListener registers).
        /// </summary>
        internal static IntPtr FindCollection(string path, out ushort usage, out ushort vendor, out ushort product)
        {
            if (OffLimitsForTest || string.IsNullOrEmpty(path)) { usage = vendor = product = 0; return IntPtr.Zero; }
            usage = vendor = product = 0;
            uint count = 0, size = (uint)Marshal.SizeOf<RawDeviceListEntry>();
            if (GetRawInputDeviceList(null, ref count, size) != 0 || count == 0) return IntPtr.Zero;
            var list = new RawDeviceListEntry[count];
            if (GetRawInputDeviceList(list, ref count, size) == unchecked((uint)-1)) return IntPtr.Zero;
            for (int i = 0; i < count; i++)
            {
                if (list[i].Type != RimTypeHid) continue;
                if (!Info(list[i].Handle, out ushort page, out ushort u, out ushort v, out ushort p) || page != 0x01 || u is not (0x04 or 0x05 or 0x08)) continue;
                if (!string.Equals(NameOf(list[i].Handle), path, StringComparison.OrdinalIgnoreCase)) continue;
                usage = u;
                vendor = v;
                product = p;
                return list[i].Handle;
            }
            return IntPtr.Zero;
        }

        private static bool Info(IntPtr handle, out ushort page, out ushort usage, out ushort vendor, out ushort product)
        {
            page = usage = vendor = product = 0;
            uint size = 32;
            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                Marshal.WriteInt32(buffer, (int)size);
                if (GetRawInputDeviceInfoW(handle, RidiDeviceInfo, buffer, ref size) == unchecked((uint)-1)) return false;
                if ((uint)Marshal.ReadInt32(buffer, 4) != RimTypeHid) return false;
                // RID_DEVICE_INFO_HID: dwVendorId, dwProductId, dwVersionNumber, usUsagePage, usUsage.
                vendor = (ushort)Marshal.ReadInt32(buffer, 8);
                product = (ushort)Marshal.ReadInt32(buffer, 12);
                page = (ushort)Marshal.ReadInt16(buffer, 20);
                usage = (ushort)Marshal.ReadInt16(buffer, 22);
                return true;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        private static string NameOf(IntPtr handle)
        {
            uint size = 0;
            GetRawInputDeviceInfoW(handle, RidiDeviceName, IntPtr.Zero, ref size);
            if (size == 0 || size > 4096) return null;
            IntPtr buffer = Marshal.AllocHGlobal((int)size * 2);
            try
            {
                if (GetRawInputDeviceInfoW(handle, RidiDeviceName, buffer, ref size) == unchecked((uint)-1)) return null;
                return Marshal.PtrToStringUni(buffer);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        /// <summary>A Raw Input handle's preparsed data, which the caller
        /// frees, or zero.</summary>
        internal static IntPtr PreparsedOf(IntPtr handle)
        {
            if (OffLimitsForTest || handle == IntPtr.Zero) return IntPtr.Zero;
            uint size = 0;
            GetRawInputDeviceInfoW(handle, RidiPreparsedData, IntPtr.Zero, ref size);
            if (size == 0) return IntPtr.Zero;
            IntPtr data = Marshal.AllocHGlobal((int)size);
            if (GetRawInputDeviceInfoW(handle, RidiPreparsedData, data, ref size) == unchecked((uint)-1))
            {
                Marshal.FreeHGlobal(data);
                return IntPtr.Zero;
            }
            return data;
        }

        /// <summary>
        /// The input controls preparsed data declares, one per usage, with
        /// their data indices, and the input report's length. HIDP_CAPS,
        /// HIDP_BUTTON_CAPS and HIDP_VALUE_CAPS as hidpi.h lays them out, the
        /// offsets RelativeHidAxes.LayoutOf reads too.
        /// </summary>
        internal static List<HidControl> ControlsOf(IntPtr preparsed, out int reportLength, out int maxData)
        {
            reportLength = maxData = 0;
            var controls = new List<HidControl>();
            var caps = new byte[64];
            if (HidP_GetCaps(preparsed, caps) != HidPSuccess) return controls;
            reportLength = BitConverter.ToUInt16(caps, 4);
            ushort buttons = BitConverter.ToUInt16(caps, 46), values = BitConverter.ToUInt16(caps, 48);
            maxData = (int)HidP_MaxDataListLength(HidPInput, preparsed);
            if (buttons > 0)
            {
                var b = new byte[buttons * 72];
                ushort n = buttons;
                if (HidP_GetButtonCaps(HidPInput, b, ref n, preparsed) == HidPSuccess)
                    for (int i = 0; i < n; i++)
                    {
                        int o = i * 72;
                        ushort page = BitConverter.ToUInt16(b, o);
                        byte report = b[o + 2];
                        ushort link = BitConverter.ToUInt16(b, o + 6);
                        bool range = b[o + 12] != 0;
                        ushort usageMin = BitConverter.ToUInt16(b, o + 56), usageMax = range ? BitConverter.ToUInt16(b, o + 58) : usageMin;
                        ushort indexMin = BitConverter.ToUInt16(b, o + 68);
                        for (int u = usageMin; u <= usageMax; u++)
                            controls.Add(new HidControl(page, (ushort)u, report, link, true, (ushort)(indexMin + (u - usageMin)), 0, 1, 1, true));
                    }
            }
            if (values > 0)
            {
                var v = new byte[values * 72];
                ushort n = values;
                if (HidP_GetValueCaps(HidPInput, v, ref n, preparsed) == HidPSuccess)
                    for (int i = 0; i < n; i++)
                    {
                        int o = i * 72;
                        ushort page = BitConverter.ToUInt16(v, o);
                        byte report = v[o + 2];
                        ushort link = BitConverter.ToUInt16(v, o + 6);
                        bool range = v[o + 12] != 0;
                        bool absolute = v[o + 15] != 0;
                        int bits = BitConverter.ToUInt16(v, o + 18);
                        int logicalMin = BitConverter.ToInt32(v, o + 40), logicalMax = BitConverter.ToInt32(v, o + 44);
                        ushort usageMin = BitConverter.ToUInt16(v, o + 56), usageMax = range ? BitConverter.ToUInt16(v, o + 58) : usageMin;
                        ushort indexMin = BitConverter.ToUInt16(v, o + 68);
                        for (int u = usageMin; u <= usageMax; u++)
                            controls.Add(new HidControl(page, (ushort)u, report, link, false, (ushort)(indexMin + (u - usageMin)), logicalMin, logicalMax, bits, absolute));
                    }
            }
            return controls;
        }

        /// <summary>
        /// One report parsed by HidP_GetData into a layout's values: every
        /// button of the report's ID known, up unless the list names it, and a
        /// value known only when the list gives it, its raw number. HidP_GetData
        /// passes over a value whose report count is above one (Wine
        /// dlls/hid/hidp.c find_all_data), and such a value is no reading of
        /// 0. False when HidP refused the report.
        /// </summary>
        internal static bool Parse(ReaderLayout layout, byte reportId, ReaderReading into)
        {
            uint length = (uint)layout.MaxData;
            if (HidP_GetData(HidPInput, layout.DataList, ref length, layout.Preparsed, layout.Report, (uint)layout.Report.Length) != HidPSuccess)
                return false;
            var controls = layout.Plan.Controls;
            for (int c = 0; c < controls.Length; c++)
                if (controls[c].ReportId == reportId)
                {
                    into.Known[c] = controls[c].IsButton;
                    into.Values[c] = 0;
                }
            for (int i = 0; i < length; i++)
            {
                // HIDP_DATA: DataIndex, Reserved, then RawValue or On.
                ushort index = BitConverter.ToUInt16(layout.DataList, i * 8);
                if (!layout.ControlOfDataIndex.TryGetValue(index, out int c)) continue;
                into.Values[c] = controls[c].IsButton ? 1u : BitConverter.ToUInt32(layout.DataList, i * 8 + 4);
                into.Known[c] = true;
            }
            return true;
        }

        // ─────────────────────────────────────────────
        //  DirectInput's own objects
        // ─────────────────────────────────────────────

        private const uint DirectInputVersion = 0x0800;
        private const uint SdlObjects = RecorderReaderMatch.DidftButton | RecorderReaderMatch.DidftAxis | RecorderReaderMatch.DidftPov;
        private const int Continue = 1;
        private static readonly IntPtr DipropRange = new(4), DipropDeadzone = new(5), DipropGuidAndPath = new(12);
        private const uint DiphDevice = 0, DiphById = 2;
        private static readonly Guid DirectInput8W = new("BF798031-483A-4DA2-AA99-5D64ED369700");

        // From System32 alone: PadForge may run with rights from a folder
        // others can write to, and dinput8.dll is no known DLL.
        [DllImport("dinput8.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int DirectInput8Create(IntPtr instance, uint version, in Guid interfaceId, out IntPtr directInput, IntPtr outer);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern IntPtr GetModuleHandle(string name);

        private static unsafe IntPtr Slot(IntPtr com, int index) => (*(IntPtr**)com)[index];

        /// <summary>
        /// PadForge's one DirectInput enumerator, for this comparison's object
        /// read and for the axes a force-feedback device's motors act on
        /// (ForceActuators.ReadAll): every attached device of a class that
        /// DirectInput lists with <paramref name="flags"/>, created one at a
        /// time and never acquired, handed with its product GUID to
        /// <paramref name="each"/>, which returns false to stop. A device whose
        /// product <paramref name="wants"/> passes over is not created. False
        /// when DirectInput cannot be asked.
        /// </summary>
        internal static unsafe bool EachDevice(uint deviceClass, uint flags, Func<Guid, bool> wants, Func<IntPtr, Guid, bool> each)
        {
            IntPtr directInput = IntPtr.Zero;
            var found = new List<(Guid Instance, Guid Product)>();
            var handle = GCHandle.Alloc(found);
            try
            {
                if (DirectInput8Create(GetModuleHandle(null), DirectInputVersion, in DirectInput8W, out directInput, IntPtr.Zero) < 0
                    || directInput == IntPtr.Zero)
                    return false;
                // IDirectInput8W: 3 CreateDevice, 4 EnumDevices.
                var enumDevices = (delegate* unmanaged[Stdcall]<IntPtr, uint, delegate* unmanaged[Stdcall]<ForceActuators.DeviceInstance*, IntPtr, int>, IntPtr, uint, int>)Slot(directInput, 4);
                if (enumDevices(directInput, deviceClass, &OnDevice, GCHandle.ToIntPtr(handle), flags) < 0) return false;
                var createDevice = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, IntPtr, int>)Slot(directInput, 3);
                foreach (var (instance, product) in found)
                {
                    if (wants != null && !wants(product)) continue;
                    var id = instance;
                    IntPtr device = IntPtr.Zero;
                    if (createDevice(directInput, &id, &device, IntPtr.Zero) < 0 || device == IntPtr.Zero) continue;
                    try
                    {
                        if (!each(device, product)) break;
                    }
                    finally { Marshal.Release(device); }
                }
                return true;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return false;
            }
            finally
            {
                handle.Free();
                if (directInput != IntPtr.Zero) Marshal.Release(directInput);
            }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static unsafe int OnDevice(ForceActuators.DeviceInstance* device, IntPtr context)
        {
            if (GCHandle.FromIntPtr(context).Target is List<(Guid, Guid)> found) found.Add((device->Instance, device->Product));
            return Continue;
        }

        /// <summary>A device's objects of <paramref name="types"/>, in
        /// DirectInput's order, or null when DirectInput refused the
        /// list.</summary>
        internal static unsafe List<ForceActuators.ObjectInstance> ObjectsOf(IntPtr device, uint types)
        {
            var listed = new List<ForceActuators.ObjectInstance>();
            var handle = GCHandle.Alloc(listed);
            try
            {
                // IDirectInputDevice8W: 4 EnumObjects.
                var enumObjects = (delegate* unmanaged[Stdcall]<IntPtr, delegate* unmanaged[Stdcall]<ForceActuators.ObjectInstance*, IntPtr, int>, IntPtr, uint, int>)Slot(device, 4);
                return enumObjects(device, &OnObject, GCHandle.ToIntPtr(handle), types) < 0 ? null : listed;
            }
            finally { handle.Free(); }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static unsafe int OnObject(ForceActuators.ObjectInstance* item, IntPtr context)
        {
            if (GCHandle.FromIntPtr(context).Target is List<ForceActuators.ObjectInstance> listed) listed.Add(*item);
            return Continue;
        }

        /// <summary>
        /// DirectInput's objects of the attached game controller at a path,
        /// listed with SDL's flags (SDL_dinputjoystick.c:1185-1187), each axis
        /// given the two properties SDL gives it (552-577) on this process's
        /// own device object, whose properties belong to it alone (Wine keeps
        /// them per device object, dlls/dinput/device.c:2252). Null when
        /// DirectInput lists no game controller at the path or cannot be
        /// asked. Never acquires the device.
        /// </summary>
        internal static unsafe List<DiObject> ReadObjects(string path)
        {
            if (OffLimitsForTest) return null;
            if (string.IsNullOrEmpty(path)) return null;
            List<DiObject> objects = null;
            EachDevice(ForceActuators.GameControllers, ForceActuators.AttachedOnly, null, (device, _) =>
            {
                if (!string.Equals(PathOfDevice(device), path, StringComparison.OrdinalIgnoreCase)) return true;
                objects = SdlObjectsOf(device);
                return false;
            });
            return objects;
        }

        /// <summary>DIPROP_GUIDANDPATH's path, as SDL reads it
        /// (SDL_dinputjoystick.c:286-303).</summary>
        private static unsafe string PathOfDevice(IntPtr device)
        {
            // DIPROPGUIDANDPATH: DIPROPHEADER (16 bytes), the class GUID, then
            // MAX_PATH wide characters.
            const int size = 16 + 16 + 260 * 2;
            byte* buffer = stackalloc byte[size];
            new Span<byte>(buffer, size).Clear();
            *(uint*)buffer = size;
            *(uint*)(buffer + 4) = 16;
            *(uint*)(buffer + 8) = 0;
            *(uint*)(buffer + 12) = DiphDevice;
            // IDirectInputDevice8W: 5 GetProperty.
            var getProperty = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, byte*, int>)Slot(device, 5);
            if (getProperty(device, DipropGuidAndPath, buffer) < 0) return null;
            return new string((char*)(buffer + 32));
        }

        /// <summary>A device's objects with SDL's flags, each axis given SDL's
        /// range and dead zone, and whether both took.</summary>
        private static unsafe List<DiObject> SdlObjectsOf(IntPtr device)
        {
            var listed = ObjectsOf(device, SdlObjects);
            if (listed == null) return null;
            // IDirectInputDevice8W: 6 SetProperty.
            var setProperty = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, byte*, int>)Slot(device, 6);
            var objects = new List<DiObject>(listed.Count);
            // DIPROPRANGE: the header by ID, then -32768 and 32767.
            // DIPROPDWORD: the header by ID, then a dead zone of 0.
            byte* range = stackalloc byte[24];
            byte* zone = stackalloc byte[20];
            foreach (var o in listed)
            {
                bool taken = false;
                if ((o.ObjectType & RecorderReaderMatch.DidftAxis) != 0 && (o.ObjectType & (RecorderReaderMatch.DidftButton | RecorderReaderMatch.DidftPov)) == 0)
                {
                    *(uint*)range = 24;
                    *(uint*)(range + 4) = 16;
                    *(uint*)(range + 8) = o.ObjectType;
                    *(uint*)(range + 12) = DiphById;
                    *(int*)(range + 16) = RecorderReaderMatch.AxisMin;
                    *(int*)(range + 20) = RecorderReaderMatch.AxisMax;
                    *(uint*)zone = 20;
                    *(uint*)(zone + 4) = 16;
                    *(uint*)(zone + 8) = o.ObjectType;
                    *(uint*)(zone + 12) = DiphById;
                    *(uint*)(zone + 16) = 0;
                    taken = setProperty(device, DipropRange, range) >= 0 && setProperty(device, DipropDeadzone, zone) >= 0;
                }
                objects.Add(new DiObject(o.Type, o.ObjectType, o.UsagePage, o.Usage, o.ReportId, o.CollectionNumber, taken));
            }
            return objects;
        }

        // ─────────────────────────────────────────────
        //  The readers of the PC
        // ─────────────────────────────────────────────

        /// <summary>A device as its layout is made from it, taken on the
        /// recorder's thread.</summary>
        internal sealed record Facts(UserDevice Device, ISdlInputDevice Live, string Guid, string Path, ushort Vendor, ushort Product,
            bool GamepadLayout, bool GamepadOpened, bool ExtraAxes, bool Touchpad, int RawAxes, int RawButtons, int NumAxes, int NumHats,
            int NumButtons, uint InstanceId, bool Hidden);

        /// <summary>Why a device whose path Raw Input lists no collection
        /// at has no second reader. HidHide cannot block Raw Input
        /// (docs.nefarius.at, docs/projects/HidHide/FAQ.md:11), so the reason
        /// is Raw Input's for a hidden device too, and its hidden setting is
        /// a fact beside it.</summary>
        internal static string NoCollectionReason(bool hidden)
            => "Raw Input lists no game controller at its path" + (hidden ? HiddenFact : string.Empty);

        /// <summary>The fact a line about a device's Raw Input reads ends
        /// with when PadForge's setting hides the device with HidHide.</summary>
        internal const string HiddenFact = ", and it is set to be hidden by HidHide";

        /// <summary>The comparison's readers of this PC: layouts made one at
        /// a time on a thread of their own, reports parsed on the recorder's
        /// thread.</summary>
        internal sealed class WindowsReaders : IReaderSource
        {
            private readonly object _sync = new();
            private readonly Dictionary<UserDevice, (ISdlInputDevice Live, ReaderLayout Layout)> _made = new();
            private readonly Queue<Facts> _asked = new();
            private readonly HashSet<UserDevice> _queued = new();
            private int _making;

            public ReaderLayout Layout(UserDevice device)
            {
                if (device?.Device is not ISdlInputDevice live) return null;
                lock (_sync)
                {
                    if (_made.TryGetValue(device, out var made) && ReferenceEquals(made.Live, live)) return made.Layout;
                    if (_queued.Contains(device)) return null;
                    _queued.Add(device);
                    _asked.Enqueue(FactsOf(device, live));
                }
                if (Interlocked.Exchange(ref _making, 1) == 0) Task.Run(MakeAll);
                return null;
            }

            private static Facts FactsOf(UserDevice device, ISdlInputDevice live) => new(
                device, live, live.SdlGuid, device.DevicePath, device.VendorId, device.ProdId,
                RecordingKinds.Of(device) == PadForge.Engine.Recording.RecordingControlKind.Gamepad, live.GamepadHandle != IntPtr.Zero,
                live.HasExtraGenericAxes, live.HasTouchpad, live.RawAxisCount, live.RawButtonCount, live.NumAxes, live.NumHats,
                live.NumButtons, live.SdlInstanceId, device.HidHideEnabled);

            private void MakeAll()
            {
                try
                {
                    while (true)
                    {
                        Facts facts;
                        lock (_sync)
                        {
                            if (_asked.Count == 0) return;
                            facts = _asked.Dequeue();
                        }
                        ReaderLayout layout;
                        try { layout = Make(facts); }
                        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException
                            or ArgumentException or MarshalDirectiveException or SEHException)
                        {
                            layout = new ReaderLayout { Reason = "Windows did not answer for it" };
                        }
                        catch (Exception ex) when (ex is not OutOfMemoryException)
                        {
                            // A defect of PadForge's own: said once in the
                            // file, never retried at every look.
                            layout = new ReaderLayout { Reason = "PadForge could not make its layout: " + ex.GetType().Name };
                        }
                        lock (_sync)
                        {
                            _queued.Remove(facts.Device);
                            // No layout: the device is asked for again at
                            // the next look.
                            if (layout == null) continue;
                            if (_made.TryGetValue(facts.Device, out var old)) Free(old.Layout);
                            _made[facts.Device] = (facts.Live, layout);
                        }
                    }
                }
                finally
                {
                    Volatile.Write(ref _making, 0);
                    bool more;
                    lock (_sync) more = _asked.Count > 0;
                    if (more && Interlocked.Exchange(ref _making, 1) == 0) Task.Run(MakeAll);
                }
            }

            internal static ReaderLayout Make(Facts f)
            {
                // A DirectInput device is known by its GUID alone, so what
                // needs no walk of Raw Input's devices is settled first: the
                // adapter's merge, and SDL's mapping, which the engine gives
                // as no answer at once while it does not run
                // (ControllerDatabaseEngine.Post). A look that finds the
                // engine stopped walks nothing, however often it comes.
                string mapping = null;
                if (RecorderReaderPlan.Signature(f.Guid) == 0)
                {
                    if (MergeReason(f.Vendor, f.Product, f.Live) is { } merged) return new ReaderLayout { Reason = merged };
                    if (f.GamepadLayout)
                    {
                        mapping = MappingOf(f.InstanceId);
                        // No answer in time is no fact about SDL: no
                        // layout yet, and the next look asks again.
                        if (ReferenceEquals(mapping, NoAnswer)) return null;
                        if (string.IsNullOrEmpty(mapping)) return new ReaderLayout { Reason = "SDL has no DirectInput mapping for it" };
                    }
                }

                IntPtr handle;
                ushort usage;
                var collection = CollectionSeam;
                if (collection != null) (handle, usage) = collection(f.Path);
                else handle = FindCollection(f.Path, out usage, out _, out _);
                bool spaceMouse = RecorderReaderPlan.Signature(f.Guid) == 'v' && handle != IntPtr.Zero && usage == 0x08
                    && f.Vendor is 0x256F or 0x046D;
                var (cls, reason) = RecorderReaderPlan.Classify(f.Guid, false, spaceMouse, f.Vendor, f.Product, f.GamepadLayout);
                if (cls == ReaderClass.None) return new ReaderLayout { Reason = reason };
                if (handle == IntPtr.Zero)
                    return new ReaderLayout { Reason = NoCollectionReason(f.Hidden) };

                IntPtr preparsed = PreparsedOf(handle);
                if (preparsed == IntPtr.Zero) return new ReaderLayout { Reason = "Raw Input gives no report layout for it" };
                // The preparsed data goes with the layout, or is freed on every
                // other way out, a throw among them.
                bool kept = false;
                try
                {
                    var controls = ControlsOf(preparsed, out int reportLength, out int maxData);
                    ReaderPlan plan;
                    if (cls == ReaderClass.DirectInput)
                    {
                        var objects = ReadObjects(f.Path);
                        if (objects == null) return new ReaderLayout { Reason = "DirectInput lists no game controller at its path" };
                        plan = RecorderReaderPlan.DirectInputPlan(objects, controls, mapping, f.GamepadLayout, f.ExtraAxes, f.Touchpad,
                            f.RawAxes, f.RawButtons, f.NumAxes, f.NumHats, f.NumButtons, f.GamepadOpened);
                    }
                    else
                    {
                        plan = RecorderReaderPlan.FixedPlan(cls,
                            cls == ReaderClass.Hidapi ? RecorderReaderPlan.ProvenFor(f.Vendor, f.Product) : RecorderReaderPlan.SpaceMouse(), controls);
                    }

                    // Only the report IDs that carry a compared control get a
                    // slot, so a report of any other ID counts as naming none
                    // of them.
                    var ids = RecorderReaderPlan.UsedReportIds(plan);
                    if (ids.Count == 0)
                    {
                        return new ReaderLayout
                        {
                            Reason = plan.LeftOut.Count > 0
                                ? "SDL's counts are not DirectInput's, " + string.Join(", ", plan.LeftOut)
                                : "its report layout names none of the controls PadForge reads",
                        };
                    }
                    var byIndex = new Dictionary<ushort, int>();
                    for (int c = 0; c < plan.Controls.Length; c++) byIndex[plan.Controls[c].DataIndex] = c;
                    var layout = new ReaderLayout
                    {
                        Plan = plan,
                        Hidden = f.Hidden,
                        Handle = handle,
                        ReportIds = new List<byte>(ids).ToArray(),
                        ReportLength = Math.Max(1, reportLength),
                        Preparsed = preparsed,
                        ControlOfDataIndex = byIndex,
                        Report = new byte[Math.Max(1, reportLength)],
                        DataList = new byte[Math.Max(1, maxData) * 8],
                        MaxData = Math.Max(1, maxData),
                    };
                    kept = true;
                    return layout;
                }
                finally
                {
                    if (!kept) Marshal.FreeHGlobal(preparsed);
                }
            }

            /// <summary>What <see cref="MappingOf"/> gives when the engine does
            /// not answer: this one instance, told apart by reference.</summary>
            internal static readonly string NoAnswer = new('?', 1);

            /// <summary>SDL's mapping text for a joystick, asked on the poll
            /// thread, the only thread that asks SDL for it
            /// (ControllerDatabaseEngine.MappingForId), as the controller
            /// database page asks it. Null when SDL has none, and
            /// <see cref="NoAnswer"/> when the engine does not answer in
            /// time.</summary>
            private static string MappingOf(uint instanceId)
                => MappingSeam is { } seam ? seam(instanceId) : ControllerDatabaseEngine.Post(_ => ControllerDatabaseEngine.MappingForId(instanceId), NoAnswer).GetAwaiter().GetResult();

            public bool Read(ReaderLayout layout, ReaderReading into)
            {
                if (layout?.Plan == null || layout.Preparsed == IntPtr.Zero) return false;
                int n = layout.Plan.Controls.Length;
                if (into.Values.Length != n)
                {
                    into.Values = new uint[n];
                    into.Known = new bool[n];
                }
                bool any = false;
                into.NamedTicks = 0;
                foreach (byte id in layout.ReportIds)
                {
                    Array.Clear(layout.Report);
                    if (!RawReportTap.TryCopy(layout.Handle, id, layout.Report, out _, out long ticks)) continue;
                    if (!Parse(layout, id, into)) continue;
                    any = true;
                    if (ticks > into.NamedTicks) into.NamedTicks = ticks;
                }
                into.OtherTicks = RawReportTap.OtherReportTicks(layout.Handle);
                into.OtherCount = RawReportTap.OtherReportCount(layout.Handle);
                return any;
            }

            public void Watch(IReadOnlyList<ReaderLayout> layouts)
            {
                List<(IntPtr, byte[], int)> collections = null;
                if (layouts != null)
                    foreach (var l in layouts)
                        if (l?.Plan != null && l.Handle != IntPtr.Zero)
                            (collections ??= new List<(IntPtr, byte[], int)>()).Add((l.Handle, l.ReportIds, l.ReportLength));
                if (collections == null)
                {
                    if (RawReportTap.Watching) RawReportTap.Clear();
                    return;
                }
                RawReportTap.Watch(collections);
            }

            public void Release(ReaderLayout layout)
            {
                if (layout == null) return;
                lock (_sync)
                {
                    UserDevice key = null;
                    foreach (var (device, made) in _made)
                        if (ReferenceEquals(made.Layout, layout)) { key = device; break; }
                    if (key == null) return;
                    _made.Remove(key);
                }
                Free(layout);
            }

            public void ReleaseAll()
            {
                List<ReaderLayout> made;
                lock (_sync)
                {
                    made = new List<ReaderLayout>(_made.Count);
                    foreach (var entry in _made.Values) made.Add(entry.Layout);
                    _made.Clear();
                }
                foreach (var layout in made) Free(layout);
            }

            private static void Free(ReaderLayout layout)
            {
                if (layout?.Preparsed is IntPtr p && p != IntPtr.Zero)
                {
                    layout.Preparsed = IntPtr.Zero;
                    Marshal.FreeHGlobal(p);
                }
            }
        }
    }
}
