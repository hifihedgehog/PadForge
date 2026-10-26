using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using PadForge.Engine;
using static PadForge.Common.Input.SonyHeadsetHid;

namespace PadForge.Common.Input
{
    /// <summary>CSL DD output discovery. Only metadata handles (access=0) are
    /// opened here; discovery never sends a report, including feature reports.</summary>
    internal static class FanatecHidEndpoint
    {
        internal sealed record Candidate(string Path, uint Parent, ushort VendorId, ushort ProductId,
            ushort UsagePage, ushort Usage, ushort OutputLength, HIDP_VALUE_CAPS[] OutputValues,
            byte[] ValidOutputIds);
        internal sealed record Endpoint(string Path, uint Parent, byte ReportId, ushort ReportLength);
        private static readonly ConcurrentDictionary<string, Endpoint> Cache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, long> RetryAt = new(StringComparer.OrdinalIgnoreCase);
        internal static bool IsCslDd(string path) => path?.Contains("vid_0eb7&pid_0020", StringComparison.OrdinalIgnoreCase) == true;

        internal static Endpoint Select(string sourcePath, IReadOnlyList<Candidate> candidates)
        {
            var source = candidates.FirstOrDefault(c => string.Equals(c.Path, sourcePath, StringComparison.OrdinalIgnoreCase));
            if (source == null || source.Parent == 0 || source.VendorId != 0x0EB7 || source.ProductId != 0x0020) return null;
            var choices = new List<Endpoint>();
            foreach (var c in candidates)
            {
                if (c.Parent != source.Parent || c.VendorId != source.VendorId || c.ProductId != source.ProductId
                    || c.UsagePage != 1 || c.Usage != 4 || c.OutputLength != 8) continue;
                foreach (var value in c.OutputValues)
                {
                    // Excludes COL03's FF/64-byte counted-buffer protocol.
                    if (value.UsagePage != 0xFF00 || value.UsageMin != 2 || value.IsRange
                        || value.BitSize != 8 || value.ReportCount != 7 || !c.ValidOutputIds.Contains(value.ReportID)) continue;
                    var endpoint = new Endpoint(c.Path, c.Parent, value.ReportID, c.OutputLength);
                    if (!choices.Contains(endpoint)) choices.Add(endpoint);
                }
            }
            // The native seven-byte command channel is the primary report,
            // irrespective of which joystick collection SDL used for input.
            var primary = choices.Where(c => c.ReportId == 1).ToArray();
            if (primary.Length == 1) return primary[0];
            return choices.Count == 1 ? choices[0] : null; // Never guess between ambiguous devices/reports.
        }

        internal static bool TryResolve(string sourcePath, out Endpoint endpoint)
        {
            if (Cache.TryGetValue(sourcePath, out endpoint)) return true;
            long now = Environment.TickCount64;
            if (RetryAt.TryGetValue(sourcePath, out long next) && now < next) return false;
            endpoint = Select(sourcePath, Enumerate());
            if (endpoint == null)
            {
                RetryAt[sourcePath] = Environment.TickCount64 + 1000;
                if (HidOutputDiagnostics.ShouldLog(sourcePath, "resolve-failed"))
                    SdlDiagLog.WriteLine($"[FanatecHidEndpoint] no unambiguous validated 8-byte command endpoint; source={sourcePath}");
                return false;
            }
            Cache[sourcePath] = endpoint;
            RetryAt.TryRemove(sourcePath, out _);
            SdlDiagLog.WriteLine($"[FanatecHidEndpoint] source={sourcePath} output={endpoint.Path} "
                + $"reportId=0x{endpoint.ReportId:X2} outputLength={endpoint.ReportLength} parent={endpoint.Parent}");
            return true;
        }

        internal static void ResetDevice(string sourcePath)
        {
            if (string.IsNullOrEmpty(sourcePath)) return;
            RetryAt.TryRemove(sourcePath, out _);
            if (!Cache.TryRemove(sourcePath, out var endpoint)) return;
            RawHidOutput.ResetDevice(endpoint.Path);
            foreach (var pair in Cache)
                if (pair.Value.Parent == endpoint.Parent) Cache.TryRemove(pair.Key, out _);
        }

        internal static List<Candidate> Enumerate()
        {
            var result = new List<Candidate>();
            HidD_GetHidGuid(out var guid);
            IntPtr set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set == new IntPtr(-1)) return result;
            try
            {
                for (uint i = 0; ; i++)
                {
                    var iface = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref iface)) break;
                    var dev = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
                    SetupDiGetDeviceInterfaceDetail(set, ref iface, IntPtr.Zero, 0, out uint needed, ref dev);
                    if (needed < 6 || needed > 65536) continue;
                    IntPtr detail = Marshal.AllocHGlobal((int)needed);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetail(set, ref iface, detail, needed, out _, ref dev)) continue;
                        string path = Marshal.PtrToStringUni(detail + 4);
                        if (!IsCslDd(path) || CM_Get_Parent(out uint parent, dev.DevInst, 0) != 0) continue;
                        using var handle = CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                        if (handle.IsInvalid || !HidD_GetPreparsedData(handle, out IntPtr pp)) continue;
                        try
                        {
                            var attr = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
                            if (!HidD_GetAttributes(handle, ref attr) || HidP_GetCaps(pp, out var caps) != HIDP_STATUS_SUCCESS) continue;
                            var values = GetValueCaps(1, pp, caps.NumberOutputValueCaps);
                            byte[] validIds = values.Select(v => v.ReportID).Distinct().Where(id => caps.OutputReportByteLength > 0
                                && AnalogKeyboardHidRuntime.HidP_InitializeReportForID(1, id, pp,
                                    new byte[caps.OutputReportByteLength], caps.OutputReportByteLength) == HIDP_STATUS_SUCCESS).ToArray();
                            result.Add(new Candidate(path, parent, attr.VendorID, attr.ProductID, caps.UsagePage, caps.Usage,
                                caps.OutputReportByteLength, values, validIds));
                        }
                        finally { HidD_FreePreparsedData(pp); }
                    }
                    finally { Marshal.FreeHGlobal(detail); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return result;
        }

        // Runs before the ordinary App startup, so the engine, updater,
        // HidHide and HIDMaestro cannot start in metadata-only probe mode.
        internal static bool TryRunProbe(string[] args, out int exitCode)
        {
            exitCode = 0;
            if (args == null || !args.Contains("--fanatec-hid-probe")) return false;
            try
            {
                int at = Array.IndexOf(args, "--fanatec-hid-probe");
                if (at + 1 >= args.Length) throw new ArgumentException("Expected an output JSON path after --fanatec-hid-probe.");
                var candidates = Enumerate();
                var report = new
                {
                    Mode = "MetadataOnly_NoOutput_NoEngine", Build = typeof(FanatecHidEndpoint).Assembly.GetName().Version?.ToString(), Utc = DateTime.UtcNow,
                    Collections = candidates.Select(c => new { c.Path, c.Parent, c.VendorId, c.ProductId, c.UsagePage, c.Usage,
                        OutputReportByteLength = c.OutputLength, OutputReportIds = c.ValidOutputIds.Select(id => $"0x{id:X2}"),
                        ResolvedOutput = Select(c.Path, candidates) })
                };
                System.IO.File.WriteAllText(args[at + 1], System.Text.Json.JsonSerializer.Serialize(report,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                exitCode = 1;
                System.Windows.MessageBox.Show(ex.Message, "Fanatec HID metadata probe");
            }
            return true;
        }

        [DllImport("cfgmgr32.dll")]
        private static extern uint CM_Get_Parent(out uint parent, uint node, uint flags);
    }
}
