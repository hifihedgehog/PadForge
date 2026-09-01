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
        /// <summary>The present-node sweep (#391) widens a record's hide
        /// list only when it is the sole record of its VID/PID, so two
        /// distinct pads of one product never hide each other.</summary>
        [Fact]
        public void SiblingSweep_OnlyForTheSoleRecordOfItsProduct()
        {
            var ds = new PadForge.Engine.Data.UserDevice { VendorId = 0x054C, ProdId = 0x0CE6 };
            var xbox = new PadForge.Engine.Data.UserDevice { VendorId = 0x045E, ProdId = 0x0B13 };
            var ds2 = new PadForge.Engine.Data.UserDevice { VendorId = 0x054C, ProdId = 0x0CE6 };

            Assert.True(PadForge.Services.InputService.HidHideSiblingSweepAllowed(new[] { ds, xbox }, ds));
            Assert.False(PadForge.Services.InputService.HidHideSiblingSweepAllowed(new[] { ds, ds2, xbox }, ds));
            Assert.True(PadForge.Services.InputService.HidHideSiblingSweepAllowed(new[] { ds, null, xbox }, ds));
            Assert.False(PadForge.Services.InputService.HidHideSiblingSweepAllowed(new[] { ds }, null));
            Assert.False(PadForge.Services.InputService.HidHideSiblingSweepAllowed(new[] { ds }, new PadForge.Engine.Data.UserDevice()));
        }

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
            string body = svc.Substring(at, 24000);
            Assert.Contains("HidHideController.TryProbe(out int hidHideErr)", body);
            Assert.Contains("HIDHIDE UNAVAILABLE: hiding requested for", body);
            Assert.Contains("HIDHIDE apply devices=", body);
            Assert.Contains("HIDHIDE dev {ud.VendorId:X4}:{ud.ProdId:X4} id={instanceId} expanded=", body);
            // The first-pass sweep of present nodes sits in the real-path
            // branch, behind the sole-record gate, and reports what it added.
            Assert.Contains("if (HidHideSiblingSweepAllowed(snapshot, ud))", body);
            Assert.Contains("foreach (var realId in FindInstanceIdsForDevice(ud))", body);
            Assert.Contains("sweep={sweep.Count}", body);
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
