using System;
using System.IO;
using System.Linq;
using PadForge.Common;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// HidHide observability (#391, discussion #388). ApplyDeviceHiding
    /// logged nothing before this, so a "the physical was not hidden"
    /// report could not be judged from a trace. These pin the read-back
    /// set difference and the source contracts that make the hiding path
    /// readable: the availability probe with its error, the per-device
    /// resolution and expansion, the sync diff with activation, and the
    /// read-back of the driver's own list.
    /// </summary>
    public class HidHideDiagnosticsTests
    {
        [Fact]
        public void ComputeMissing_IsACaseInsensitiveSetDifference()
        {
            var desired = new[] { @"HID\VID_054C&PID_0CE6&MI_03\8&1", @"USB\VID_054C&PID_0CE6&MI_03\7&1" };
            var present = new[] { @"hid\vid_054c&pid_0ce6&mi_03\8&1" };
            var missing = HidHideController.ComputeMissing(desired, present);
            Assert.Single(missing);
            Assert.Equal(desired[1], missing[0]);

            Assert.Empty(HidHideController.ComputeMissing(desired, desired));
            Assert.Empty(HidHideController.ComputeMissing(Array.Empty<string>(), null));
            Assert.Equal(2, HidHideController.ComputeMissing(desired, null).Count);
        }

        /// <summary>The controller exposes the probe with its Win32 error,
        /// the read-back, and the sync overload that reports its diff.</summary>
        [Fact]
        public void Controller_ExposesTheDiagnosticSurface()
        {
            string ctl = RepoText("PadForge.App", "Common", "HidHideController.cs");
            Assert.Contains("public static bool TryProbe(out int win32Error)", ctl);
            Assert.Contains("win32Error = Marshal.GetLastWin32Error();", ctl);
            Assert.Contains("public static List<string> MissingFromBlacklist(", ctl);
            Assert.Contains("public static void SyncManagedDevices(HashSet<string> desiredIds, out List<string> added, out List<string> removed)", ctl);
        }

        /// <summary>The hiding path logs: the unavailable case with its
        /// error, the per-device resolution and expansion, the sync diff
        /// with activation, and the read-back with any missing ids.</summary>
        [Fact]
        public void ApplyDeviceHiding_IsObservable()
        {
            string svc = RepoText("PadForge.App", "Services", "InputService.cs");
            int at = svc.IndexOf("public void ApplyDeviceHiding()", StringComparison.Ordinal);
            Assert.True(at > 0);
            string body = svc.Substring(at, 12000);
            Assert.Contains("HidHideController.TryProbe(out int hidHideErr)", body);
            Assert.Contains("HIDHIDE UNAVAILABLE: hiding requested for", body);
            Assert.Contains("HIDHIDE apply devices=", body);
            Assert.Contains("HIDHIDE dev {ud.VendorId:X4}:{ud.ProdId:X4} id={instanceId} expanded=", body);
            Assert.Contains("HIDHIDE dev {ud.VendorId:X4}:{ud.ProdId:X4} synthetic path=", body);
            Assert.Contains("SyncManagedDevices(desiredIds, out var added, out var removed)", body);
            Assert.Contains("MissingFromBlacklist(desiredIds)", body);
            Assert.Contains("readback=MISSING", body);
            // The unavailable branch fires only when hiding was wanted.
            Assert.Contains("if (!hidHideUp && wantHiding > 0)", body);
            // The per-row gate still keys on the install scan, unchanged.
            Assert.Contains("row.IsHidHideAvailable = _mainVm.Settings.IsHidHideInstalled;", svc);
        }

        private static string RepoText(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
        }
    }
}
