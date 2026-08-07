using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Native HID plumbing for the Sony headset head tracker (issue #188):
    /// the enable-sequence writer, descriptor-driven input readers, and the
    /// P/Invoke surface they share with the enumeration runtime. Every call
    /// sequence is ported from the reference implementation
    /// NicholasSlattery/sony-head-tracker (MIT), hid_backend.cpp.
    /// </summary>
    internal static class SonyHeadsetHid
    {
        internal const uint HIDP_STATUS_SUCCESS = 0x00110000;
        internal const int HidP_Input = 0;
        internal const int HidP_Feature = 2;
        internal const int ERROR_IO_PENDING = 997;
        internal const uint GENERIC_READ = 0x80000000;
        internal const uint GENERIC_WRITE = 0x40000000;
        internal const uint FILE_SHARE_READ = 0x00000001;
        internal const uint FILE_SHARE_WRITE = 0x00000002;
        internal const uint OPEN_EXISTING = 3;
        internal const uint FILE_FLAG_OVERLAPPED = 0x40000000;

        // ─────────────────────────────────────────────
        //  Field classification for the parse loop
        // ─────────────────────────────────────────────

        internal enum FieldKind
        {
            Other = 0,
            Rotation,      // 0x0544 packed vector
            GyroVector,    // 0x0545 / 0x0456 packed vector
            GyroScalar,    // 0x0457-0x0459 per-axis
            AccelVector,   // 0x0452 packed vector
            AccelScalar,   // 0x0453-0x0455 per-axis
            ResetCounter   // 0x0546
        }

        /// <summary>One input value field the reader consumes: its HidP
        /// addressing plus the pure scaling facts for packed decode.</summary>
        internal struct ParsedField
        {
            public FieldKind Kind;
            public byte ReportId;
            public ushort UsagePage;
            public ushort Usage;
            public ushort LinkCollection;
            public int Axis; // 0..2 for the scalar kinds
            public HeadTrackerHid.FieldScale Scale;
        }

        /// <summary>Classifies the descriptor's input value caps into the
        /// fields the reader loop consumes (reference parse loop,
        /// hid_backend.cpp connect callback).</summary>
        internal static ParsedField[] BuildParsedFields(HIDP_VALUE_CAPS[] inputValues)
        {
            var fields = new List<ParsedField>(inputValues.Length);
            foreach (var c in inputValues)
            {
                if (c.UsagePage != HeadTrackerHid.SensorPage) continue;
                ushort usage = c.UsageMin; // NotRange.Usage aliases UsageMin
                var kind = usage switch
                {
                    HeadTrackerHid.Rotation => FieldKind.Rotation,
                    HeadTrackerHid.AngularVelocity => FieldKind.GyroVector,
                    HeadTrackerHid.AngularVelocityVector => FieldKind.GyroVector,
                    HeadTrackerHid.AccelerationVector => FieldKind.AccelVector,
                    >= HeadTrackerHid.AngularVelocityX and <= HeadTrackerHid.AngularVelocityZ => FieldKind.GyroScalar,
                    >= HeadTrackerHid.AccelerationX and <= HeadTrackerHid.AccelerationZ => FieldKind.AccelScalar,
                    HeadTrackerHid.ResetCounter => FieldKind.ResetCounter,
                    _ => FieldKind.Other
                };
                if (kind == FieldKind.Other) continue;
                int axis = kind == FieldKind.GyroScalar ? usage - HeadTrackerHid.AngularVelocityX
                    : kind == FieldKind.AccelScalar ? usage - HeadTrackerHid.AccelerationX
                    : 0;
                fields.Add(new ParsedField
                {
                    Kind = kind,
                    ReportId = c.ReportID,
                    UsagePage = c.UsagePage,
                    Usage = usage,
                    LinkCollection = c.LinkCollection,
                    Axis = axis,
                    Scale = new HeadTrackerHid.FieldScale
                    {
                        BitSize = c.BitSize,
                        ReportCount = c.ReportCount,
                        LogicalMin = c.LogicalMin,
                        LogicalMax = c.LogicalMax,
                        PhysicalMin = c.PhysicalMin,
                        PhysicalMax = c.PhysicalMax,
                        UnitExponent = HeadTrackerHid.DecodeUnitExponent(c.UnitsExp)
                    }
                });
            }
            return fields.ToArray();
        }

        /// <summary>Reads a 3-value packed vector field
        /// (reference usageArray): HidP_GetUsageValueArray into the packed
        /// buffer, then the pure LSB-first sign-extending decode.</summary>
        internal static bool ReadVector(IntPtr preparsed, in ParsedField field,
            byte[] report, int reportLength, double[] values)
        {
            int byteCount = ((field.Scale.ReportCount * field.Scale.BitSize) + 7) / 8;
            if (byteCount <= 0 || byteCount > 64) return false;
            var packed = new byte[byteCount];
            uint status = HidP_GetUsageValueArray(HidP_Input, field.UsagePage,
                field.LinkCollection, field.Usage, packed, (ushort)byteCount,
                preparsed, report, (uint)reportLength);
            if (status != HIDP_STATUS_SUCCESS) return false;
            var decoded = HeadTrackerHid.DecodePackedValues(packed, in field.Scale);
            if (decoded.Length < 3) return false;
            values[0] = decoded[0]; values[1] = decoded[1]; values[2] = decoded[2];
            return true;
        }

        /// <summary>Reads one scaled scalar (reference scalarValue):
        /// HidP_GetScaledUsageValue first, then the raw-value path with
        /// manual sign extension and descriptor scaling.</summary>
        internal static bool ReadScalar(IntPtr preparsed, in ParsedField field,
            byte[] report, int reportLength, out double value)
        {
            value = 0;
            if (HidP_GetScaledUsageValue(HidP_Input, field.UsagePage,
                field.LinkCollection, field.Usage, out int scaled,
                preparsed, report, (uint)reportLength) == HIDP_STATUS_SUCCESS)
            {
                value = scaled;
                return true;
            }
            if (HidP_GetUsageValue(HidP_Input, field.UsagePage,
                field.LinkCollection, field.Usage, out uint raw,
                preparsed, report, (uint)reportLength) != HIDP_STATUS_SUCCESS)
                return false;
            long v = raw;
            if (field.Scale.LogicalMin < 0 && field.Scale.BitSize > 0 && field.Scale.BitSize < 64)
            {
                ulong sign = 1UL << (field.Scale.BitSize - 1);
                ulong mask = (1UL << field.Scale.BitSize) - 1;
                v = (long)((((ulong)raw & mask) ^ sign) - sign);
            }
            value = HeadTrackerHid.Scale(v, field.Scale.LogicalMin, field.Scale.LogicalMax,
                field.Scale.PhysicalMin, field.Scale.PhysicalMax, field.Scale.UnitExponent);
            return true;
        }

        // ─────────────────────────────────────────────
        //  Enable sequence (reference configureHeadTrackerFeatures)
        // ─────────────────────────────────────────────

        /// <summary>
        /// Builds ONE combined feature buffer per report ID so a write
        /// cannot clobber a sibling field in the same report, encodes the
        /// report interval (protocol target 10-20 ms, or the device's
        /// fastest advertised interval when its range cannot reach that),
        /// sets the Power Full and All Events selectors (both mandatory)
        /// and the v2 ACL transport selector (optional, v1 descriptors
        /// lack it), then HidD_SetFeature per buffer. Any failure aborts.
        /// </summary>
        internal static bool ConfigureHeadTrackerFeatures(SafeFileHandle handle, IntPtr preparsed,
            in HIDP_CAPS caps, HIDP_VALUE_CAPS[] featureValues, HIDP_BUTTON_CAPS[] featureButtons)
        {
            ushort featureLength = caps.FeatureReportByteLength;
            var reports = new Dictionary<byte, byte[]>();
            byte[] Ensure(byte id)
            {
                if (!reports.TryGetValue(id, out var report))
                {
                    report = new byte[featureLength];
                    report[0] = id;
                    reports[id] = report;
                }
                return report;
            }

            foreach (var c in featureValues)
            {
                ushort usage = c.UsageMin; // NotRange.Usage aliases UsageMin
                if (c.UsagePage != HeadTrackerHid.SensorPage || usage != HeadTrackerHid.ReportInterval)
                    continue;
                var report = Ensure(c.ReportID);
                long target = HeadTrackerHid.ComputeIntervalTarget(
                    c.PhysicalMin, c.PhysicalMax, HeadTrackerHid.DecodeUnitExponent(c.UnitsExp));
                uint status = HidP_SetScaledUsageValue(HidP_Feature, c.UsagePage,
                    c.LinkCollection, usage, (int)target, preparsed, report, (uint)report.Length);
                if (status != HIDP_STATUS_SUCCESS) return false;
            }

            // Selector order mirrors the reference: transport first, then
            // power, then reporting mode. Only the transport is optional.
            ReadOnlySpan<ushort> desired = stackalloc ushort[]
            {
                HeadTrackerHid.TransportAcl,
                HeadTrackerHid.PowerFull,
                HeadTrackerHid.ReportingAllEvents
            };
            foreach (ushort usage in desired)
            {
                bool exposed = false;
                foreach (var b in featureButtons)
                {
                    ushort min = b.UsageMin;
                    ushort max = b.IsRange ? b.UsageMax : b.UsageMin;
                    if (b.UsagePage != HeadTrackerHid.SensorPage || usage < min || usage > max)
                        continue;
                    exposed = true;
                    var report = Ensure(b.ReportID);
                    uint count = 1;
                    ushort mutableUsage = usage;
                    uint status = HidP_SetUsages(HidP_Feature, b.UsagePage, b.LinkCollection,
                        ref mutableUsage, ref count, preparsed, report, (uint)report.Length);
                    if (status != HIDP_STATUS_SUCCESS) return false;
                    break;
                }
                if (!exposed && usage != HeadTrackerHid.TransportAcl) return false;
            }

            foreach (var kvp in reports)
                if (!HidD_SetFeature(handle, kvp.Value, (uint)kvp.Value.Length))
                    return false;
            return reports.Count > 0;
        }

        // ─────────────────────────────────────────────
        //  Caps queries
        // ─────────────────────────────────────────────

        internal static HIDP_VALUE_CAPS[] GetValueCaps(int reportType, IntPtr preparsed, ushort count)
        {
            if (count == 0) return Array.Empty<HIDP_VALUE_CAPS>();
            var caps = new HIDP_VALUE_CAPS[count];
            ushort n = count;
            if (HidP_GetValueCaps(reportType, caps, ref n, preparsed) != HIDP_STATUS_SUCCESS)
                return Array.Empty<HIDP_VALUE_CAPS>();
            if (n < count) Array.Resize(ref caps, n);
            return caps;
        }

        internal static HIDP_BUTTON_CAPS[] GetButtonCaps(int reportType, IntPtr preparsed, ushort count)
        {
            if (count == 0) return Array.Empty<HIDP_BUTTON_CAPS>();
            var caps = new HIDP_BUTTON_CAPS[count];
            ushort n = count;
            if (HidP_GetButtonCaps(reportType, caps, ref n, preparsed) != HIDP_STATUS_SUCCESS)
                return Array.Empty<HIDP_BUTTON_CAPS>();
            if (n < count) Array.Resize(ref caps, n);
            return caps;
        }

        // ─────────────────────────────────────────────
        //  Sensor-description marker probe
        //  (reference extractDescription, both fallbacks)
        // ─────────────────────────────────────────────

        internal static string ExtractDescription(SafeFileHandle handle, IntPtr preparsed,
            in HIDP_CAPS caps, HIDP_VALUE_CAPS[] featureValues)
        {
            byte[] markerBytes = Encoding.ASCII.GetBytes(HeadTrackerHid.Marker);

            foreach (var c in featureValues)
            {
                ushort usage = c.UsageMin;
                if (c.UsagePage != HeadTrackerHid.SensorPage || usage != HeadTrackerHid.SensorDescription)
                    continue;
                var report = new byte[caps.FeatureReportByteLength];
                report[0] = c.ReportID;
                if (!HidD_GetFeature(handle, report, (uint)report.Length)) continue;

                int byteCount = ((c.ReportCount * c.BitSize) + 7) / 8;
                if (byteCount > 0 && byteCount <= 4096)
                {
                    var value = new byte[byteCount];
                    uint status = HidP_GetUsageValueArray(HidP_Feature, c.UsagePage,
                        c.LinkCollection, usage, value, (ushort)byteCount,
                        preparsed, report, (uint)report.Length);
                    if (status == HIDP_STATUS_SUCCESS)
                        return HeadTrackerHid.TrimDescription(value);
                }
                // Constant sensor-description fields are hidden by some
                // Windows HID parser versions: raw marker search.
                int at = IndexOf(report, markerBytes);
                if (at >= 0)
                    return HeadTrackerHid.TrimDescription(report.AsSpan(at));
            }

            // Some Sensor stacks omit constant fields from value caps
            // entirely: probe only descriptor-listed report IDs, never
            // guessed numeric IDs.
            var reportIds = new SortedSet<byte>();
            foreach (var c in featureValues) reportIds.Add(c.ReportID);
            foreach (byte reportId in reportIds)
            {
                var report = new byte[caps.FeatureReportByteLength];
                report[0] = reportId;
                if (!HidD_GetFeature(handle, report, (uint)report.Length)) continue;
                int at = IndexOf(report, markerBytes);
                if (at >= 0)
                    return HeadTrackerHid.TrimDescription(report.AsSpan(at));
            }
            return string.Empty;
        }

        private static int IndexOf(byte[] haystack, byte[] needle)
        {
            for (int i = 0; i + needle.Length <= haystack.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                    if (haystack[i + j] != needle[j]) { match = false; break; }
                if (match) return i;
            }
            return -1;
        }

        // ─────────────────────────────────────────────
        //  Structs
        // ─────────────────────────────────────────────

        [StructLayout(LayoutKind.Sequential)]
        internal struct HIDP_CAPS
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        // Layout per Windows SDK 10.0.26100 hidpi.h. The trailing union is
        // flattened as the Range fields; NotRange.Usage aliases UsageMin.
        [StructLayout(LayoutKind.Sequential)]
        internal struct HIDP_VALUE_CAPS
        {
            public ushort UsagePage;
            public byte ReportID;
            [MarshalAs(UnmanagedType.U1)] public bool IsAlias;
            public ushort BitField;
            public ushort LinkCollection;
            public ushort LinkUsage;
            public ushort LinkUsagePage;
            [MarshalAs(UnmanagedType.U1)] public bool IsRange;
            [MarshalAs(UnmanagedType.U1)] public bool IsStringRange;
            [MarshalAs(UnmanagedType.U1)] public bool IsDesignatorRange;
            [MarshalAs(UnmanagedType.U1)] public bool IsAbsolute;
            [MarshalAs(UnmanagedType.U1)] public bool HasNull;
            public byte Reserved;
            public ushort BitSize;
            public ushort ReportCount;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 5)]
            public ushort[] Reserved2;
            public uint UnitsExp;
            public uint Units;
            public int LogicalMin;
            public int LogicalMax;
            public int PhysicalMin;
            public int PhysicalMax;
            public ushort UsageMin;
            public ushort UsageMax;
            public ushort StringMin;
            public ushort StringMax;
            public ushort DesignatorMin;
            public ushort DesignatorMax;
            public ushort DataIndexMin;
            public ushort DataIndexMax;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct HIDP_BUTTON_CAPS
        {
            public ushort UsagePage;
            public byte ReportID;
            [MarshalAs(UnmanagedType.U1)] public bool IsAlias;
            public ushort BitField;
            public ushort LinkCollection;
            public ushort LinkUsage;
            public ushort LinkUsagePage;
            [MarshalAs(UnmanagedType.U1)] public bool IsRange;
            [MarshalAs(UnmanagedType.U1)] public bool IsStringRange;
            [MarshalAs(UnmanagedType.U1)] public bool IsDesignatorRange;
            [MarshalAs(UnmanagedType.U1)] public bool IsAbsolute;
            public ushort ReportCount;
            public ushort Reserved2;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 9)]
            public uint[] Reserved;
            public ushort UsageMin;
            public ushort UsageMax;
            public ushort StringMin;
            public ushort StringMax;
            public ushort DesignatorMin;
            public ushort DesignatorMax;
            public ushort DataIndexMin;
            public ushort DataIndexMax;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct HIDD_ATTRIBUTES
        {
            public int Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        // ─────────────────────────────────────────────
        //  P/Invoke
        // ─────────────────────────────────────────────

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
        internal static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess,
            uint shareMode, IntPtr securityAttributes, uint creationDisposition,
            uint flagsAndAttributes, IntPtr templateFile);

        // The read buffer and OVERLAPPED stay owned by the kernel until the
        // I/O completes, long after the P/Invoke returns, so both cross as
        // raw pinned pointers (the caller pins), never as marshaled arrays.
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool ReadFile(SafeFileHandle handle, IntPtr buffer,
            uint bytesToRead, out uint bytesRead, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool GetOverlappedResult(SafeFileHandle handle,
            IntPtr overlapped, out uint bytesTransferred, bool wait);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool CancelIoEx(SafeFileHandle handle, IntPtr overlapped);

        [DllImport("hid.dll")]
        internal static extern void HidD_GetHidGuid(out Guid hidGuid);

        [DllImport("hid.dll")]
        internal static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr preparsedData);

        [DllImport("hid.dll")]
        internal static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

        [DllImport("hid.dll")]
        internal static extern uint HidP_GetCaps(IntPtr preparsedData, out HIDP_CAPS capabilities);

        [DllImport("hid.dll")]
        internal static extern uint HidP_GetValueCaps(int reportType,
            [Out] HIDP_VALUE_CAPS[] valueCaps, ref ushort valueCapsLength, IntPtr preparsedData);

        [DllImport("hid.dll")]
        internal static extern uint HidP_GetButtonCaps(int reportType,
            [Out] HIDP_BUTTON_CAPS[] buttonCaps, ref ushort buttonCapsLength, IntPtr preparsedData);

        [DllImport("hid.dll")]
        internal static extern bool HidD_GetAttributes(SafeFileHandle handle, ref HIDD_ATTRIBUTES attributes);

        [DllImport("hid.dll", CharSet = CharSet.Unicode)]
        internal static extern bool HidD_GetProductString(SafeFileHandle handle,
            [Out] byte[] buffer, uint bufferLength);

        [DllImport("hid.dll")]
        internal static extern bool HidD_GetFeature(SafeFileHandle handle,
            [In, Out] byte[] reportBuffer, uint reportBufferLength);

        [DllImport("hid.dll")]
        internal static extern bool HidD_SetFeature(SafeFileHandle handle,
            [In] byte[] reportBuffer, uint reportBufferLength);

        [DllImport("hid.dll")]
        internal static extern uint HidP_GetUsageValueArray(int reportType, ushort usagePage,
            ushort linkCollection, ushort usage, [Out] byte[] usageValue, ushort usageValueByteLength,
            IntPtr preparsedData, [In] byte[] report, uint reportLength);

        [DllImport("hid.dll")]
        internal static extern uint HidP_GetScaledUsageValue(int reportType, ushort usagePage,
            ushort linkCollection, ushort usage, out int usageValue,
            IntPtr preparsedData, [In] byte[] report, uint reportLength);

        [DllImport("hid.dll")]
        internal static extern uint HidP_GetUsageValue(int reportType, ushort usagePage,
            ushort linkCollection, ushort usage, out uint usageValue,
            IntPtr preparsedData, [In] byte[] report, uint reportLength);

        [DllImport("hid.dll")]
        internal static extern uint HidP_SetScaledUsageValue(int reportType, ushort usagePage,
            ushort linkCollection, ushort usage, int usageValue,
            IntPtr preparsedData, [In, Out] byte[] report, uint reportLength);

        [DllImport("hid.dll")]
        internal static extern uint HidP_SetUsages(int reportType, ushort usagePage,
            ushort linkCollection, ref ushort usageList, ref uint usageLength,
            IntPtr preparsedData, [In, Out] byte[] report, uint reportLength);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator,
            IntPtr parent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        internal static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData,
            ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet,
            ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData,
            uint deviceInterfaceDetailDataSize, out uint requiredSize, IntPtr deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [StructLayout(LayoutKind.Sequential)]
        internal struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public uint Flags;
            public IntPtr Reserved;
        }

        internal const uint DIGCF_PRESENT = 0x02;
        internal const uint DIGCF_DEVICEINTERFACE = 0x10;
    }

    /// <summary>
    /// Enumerates present Android Head Tracker HID collections (issue
    /// #188). Discovery has no VID/PID list, matching the reference: a
    /// candidate is any HID top-level collection with usage page 0x0020
    /// (Sensor) and usage 0x00E1 (Other: Custom) whose sensor-description
    /// feature report starts with #AndroidHeadTracker#, which covers the
    /// whole compatible Sony range, current and future. Per-path verdicts
    /// are cached so the marker probe (a Bluetooth feature-report read)
    /// runs once per new path, not every sweep. All calls are blocking
    /// device I/O and belong on the background sweep worker.
    /// </summary>
    internal static class SonyHeadsetMotionRuntime
    {
        internal sealed class Candidate
        {
            public string Path;
            public string Name;
            public ushort VendorId;
            public ushort ProductId;
            public bool HasAccel;
        }

        // path (ordinal-insensitive) → qualified candidate, or null for a
        // probed-and-rejected path. Vanished paths are pruned each sweep so
        // a re-created node is probed fresh.
        private static readonly Dictionary<string, Candidate> _verdicts =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Clears cached verdicts (test seam / driver repair, so a
        /// rebound node is re-probed rather than trusted stale).</summary>
        internal static void InvalidateCache()
        {
            lock (_verdicts) _verdicts.Clear();
        }

        /// <summary>Returns the present, qualified head-tracker collections,
        /// or null when the enumeration itself failed. Null and "no devices"
        /// must stay distinct: an empty list retires every opened headset
        /// (the headset is off), while a transient SetupAPI failure must
        /// not churn live devices through a dispose-and-reopen.</summary>
        internal static List<Candidate> Enumerate()
        {
            var result = new List<Candidate>();
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

                    Candidate verdict;
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

            // Prune vanished paths so re-created nodes are probed fresh.
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
                // cbSize is the FIXED part of SP_DEVICE_INTERFACE_DETAIL_DATA_W:
                // 4-byte cbSize + one wchar, padded (8 on x64).
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

        /// <summary>
        /// Opens one path and decides whether it is an Android Head Tracker
        /// (reference enumerate loop): open RW, then R, then query-only;
        /// require sensor page 0x20 / usage 0xE1; verify the description
        /// marker. Returns null for a non-candidate.
        /// </summary>
        private static Candidate Probe(string path)
        {
            var handle = SonyHeadsetHid.CreateFile(path,
                SonyHeadsetHid.GENERIC_READ | SonyHeadsetHid.GENERIC_WRITE,
                SonyHeadsetHid.FILE_SHARE_READ | SonyHeadsetHid.FILE_SHARE_WRITE,
                IntPtr.Zero, SonyHeadsetHid.OPEN_EXISTING, 0, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                handle = SonyHeadsetHid.CreateFile(path, SonyHeadsetHid.GENERIC_READ,
                    SonyHeadsetHid.FILE_SHARE_READ | SonyHeadsetHid.FILE_SHARE_WRITE,
                    IntPtr.Zero, SonyHeadsetHid.OPEN_EXISTING, 0, IntPtr.Zero);
            }
            if (handle.IsInvalid)
            {
                handle.Dispose();
                handle = SonyHeadsetHid.CreateFile(path, 0,
                    SonyHeadsetHid.FILE_SHARE_READ | SonyHeadsetHid.FILE_SHARE_WRITE,
                    IntPtr.Zero, SonyHeadsetHid.OPEN_EXISTING, 0, IntPtr.Zero);
            }
            if (handle.IsInvalid) { handle.Dispose(); return null; }

            IntPtr preparsed = IntPtr.Zero;
            try
            {
                if (!SonyHeadsetHid.HidD_GetPreparsedData(handle, out preparsed)) return null;
                if (SonyHeadsetHid.HidP_GetCaps(preparsed, out var caps) != SonyHeadsetHid.HIDP_STATUS_SUCCESS)
                    return null;
                if (caps.UsagePage != HeadTrackerHid.SensorPage || caps.Usage != HeadTrackerHid.OtherCustom)
                    return null;

                var featureValues = SonyHeadsetHid.GetValueCaps(
                    SonyHeadsetHid.HidP_Feature, preparsed, caps.NumberFeatureValueCaps);
                string description = SonyHeadsetHid.ExtractDescription(handle, preparsed, in caps, featureValues);
                if (!description.StartsWith(HeadTrackerHid.Marker, StringComparison.Ordinal))
                    return null;

                var attributes = new SonyHeadsetHid.HIDD_ATTRIBUTES
                {
                    Size = Marshal.SizeOf<SonyHeadsetHid.HIDD_ATTRIBUTES>()
                };
                SonyHeadsetHid.HidD_GetAttributes(handle, ref attributes);

                string name = ReadProductString(handle);
                if (string.IsNullOrWhiteSpace(name)) name = "Sony Headset Tracker";

                // Accelerometer is optional in the protocol; advertise it
                // only when the descriptor really exposes it (issue #188:
                // gyro guaranteed, accel best-effort).
                bool hasAccel = false;
                var inputValues = SonyHeadsetHid.GetValueCaps(
                    SonyHeadsetHid.HidP_Input, preparsed, caps.NumberInputValueCaps);
                foreach (var c in inputValues)
                {
                    if (c.UsagePage != HeadTrackerHid.SensorPage) continue;
                    if (c.UsageMin >= HeadTrackerHid.AccelerationVector
                        && c.UsageMin <= HeadTrackerHid.AccelerationZ)
                    { hasAccel = true; break; }
                }

                return new Candidate
                {
                    Path = path,
                    Name = name,
                    VendorId = attributes.VendorID,
                    ProductId = attributes.ProductID,
                    HasAccel = hasAccel
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
            if (!SonyHeadsetHid.HidD_GetProductString(handle, buffer, (uint)buffer.Length))
                return null;
            string s = Encoding.Unicode.GetString(buffer);
            int nul = s.IndexOf('\0');
            return nul >= 0 ? s.Substring(0, nul) : s;
        }
    }
}
