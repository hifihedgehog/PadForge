using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HIDMaestro;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Removing HIDMaestro from Settings (#518): HIDMaestro 2.0.1's
    /// HMUninstall behind a preview. The engine stops before the removal and
    /// is never started by it, the status line says how it ended, and the
    /// dialog lists what a preview found or what a removal could not take.
    /// The test host never runs the removal (TestHostFenceTests,
    /// HIDMaestro_IsNotRemoved), so these tests hand it a seam.
    /// </summary>
    public class HIDMaestroRemovalTests
    {
        private const BindingFlags Internal = BindingFlags.Instance | BindingFlags.NonPublic;

        internal static HMUninstallEntry Entry(string kind, string location, HMUninstallOutcome outcome, string reason = null) =>
            (HMUninstallEntry)Activator.CreateInstance(typeof(HMUninstallEntry), Internal, null,
                new object[] { kind, location, outcome, reason }, null);

        internal static HMRunningProgram Program(int processId, string imagePath) =>
            (HMRunningProgram)Activator.CreateInstance(typeof(HMRunningProgram), Internal, null,
                new object[] { processId, imagePath, "2.0.2" }, null);

        internal static HMUninstallReport Report(bool preview, HMUninstallStatus status,
            HMRunningProgram[] programs, params HMUninstallEntry[] entries) =>
            (HMUninstallReport)Activator.CreateInstance(typeof(HMUninstallReport), Internal, null,
                new object[] { preview, status, programs, entries }, null);

        /// <summary>The engine's stop runs first, then the removal, with the
        /// usbip-win2 choice the user confirmed. With no stop to run, the
        /// removal runs alone.</summary>
        [Fact]
        public void TheEngineStopsBeforeTheRemoval()
        {
            var order = new List<string>();
            HMUninstallOptions seen = null;
            var report = Report(false, HMUninstallStatus.Removed, Array.Empty<HMRunningProgram>());
            HIDMaestroRemoval.RunSeam = o => { order.Add("remove"); seen = o; return report; };
            try
            {
                Assert.Same(report, HIDMaestroRemoval.Run(includeUsbip: true, () => order.Add("stop")));
                Assert.Equal(new[] { "stop", "remove" }, order);
                Assert.True(seen.IncludeUsbip);

                order.Clear();
                Assert.Same(report, HIDMaestroRemoval.Run(includeUsbip: false, stopEngine: null));
                Assert.Equal(new[] { "remove" }, order);
                Assert.False(seen.IncludeUsbip);
            }
            finally { HIDMaestroRemoval.RunSeam = null; }
        }

        /// <summary>The preview reads the PC with the same choice.</summary>
        [Fact]
        public void ThePreviewCarriesTheUsbipChoice()
        {
            var report = Report(true, HMUninstallStatus.Previewed, Array.Empty<HMRunningProgram>());
            var asked = new List<bool>();
            HIDMaestroRemoval.PreviewSeam = o => { asked.Add(o.IncludeUsbip); return report; };
            try
            {
                Assert.Same(report, HIDMaestroRemoval.Preview(includeUsbip: false));
                Assert.Same(report, HIDMaestroRemoval.Preview(includeUsbip: true));
                Assert.Equal(new[] { false, true }, asked);
            }
            finally { HIDMaestroRemoval.PreviewSeam = null; }
        }

        /// <summary>Each way a removal ends has its own status line. A
        /// blocked one names the programs to close, by file name.</summary>
        [Fact]
        public void TheStatusLineSaysHowTheRemovalEnded()
        {
            var none = Array.Empty<HMRunningProgram>();
            Assert.Equal(Strings.Instance.Status_HIDMaestroRemoved,
                HIDMaestroRemoval.StatusLine(Report(false, HMUninstallStatus.Removed, none)));
            Assert.Equal(Strings.Instance.Status_HIDMaestroRemovedRestart,
                HIDMaestroRemoval.StatusLine(Report(false, HMUninstallStatus.RemovedRestartPending, none)));
            Assert.Equal(Strings.Instance.Status_HIDMaestroRemovalFailed,
                HIDMaestroRemoval.StatusLine(Report(false, HMUninstallStatus.Failed, none,
                    Entry("folder", @"C:\ProgramData\HIDMaestro", HMUninstallOutcome.Failed, "in use (Win32 error 32)"))));

            var blocked = Report(false, HMUninstallStatus.Blocked,
                new[] { Program(4528, @"C:\Tools\DS4Windows\DS4Windows.exe"), Program(77, null) });
            Assert.Equal(string.Format(Strings.Instance.Status_HIDMaestroRemovalBlocked_Format,
                    "DS4Windows.exe, " + string.Format(Strings.Instance.HIDMaestroUninstall_Process_Format, "77")),
                HIDMaestroRemoval.StatusLine(blocked));
        }

        /// <summary>A program is named by its executable, or by its process
        /// ID when Windows would not give the path, and "?" when HIDMaestro
        /// knows it runs but could not name it.</summary>
        [Fact]
        public void ABlockingProgramIsNamedAsWellAsWindowsAllows()
        {
            var named = Program(12, @"C:\Games\Handheld Companion\HandheldCompanion.exe");
            Assert.Equal("HandheldCompanion.exe", HIDMaestroRemoval.ProgramName(named, fullPath: false));
            Assert.Equal(@"C:\Games\Handheld Companion\HandheldCompanion.exe", HIDMaestroRemoval.ProgramName(named, fullPath: true));
            Assert.Equal(string.Format(Strings.Instance.HIDMaestroUninstall_Process_Format, "?"),
                HIDMaestroRemoval.ProgramName(Program(0, null), fullPath: true));
        }

        /// <summary>A preview lists every item, with the reason beside each
        /// one that stays or could not be read.</summary>
        [Fact]
        public void ThePreviewListsEveryItemAndWhyOneStays()
        {
            var rows = UninstallHIDMaestroDialog.RowsFor(Report(true, HMUninstallStatus.Failed, Array.Empty<HMRunningProgram>(),
                Entry("driver package", "oem5.inf (hidmaestro.inf)", HMUninstallOutcome.WouldRemove),
                Entry("service setting", "GameInputSvc", HMUninstallOutcome.Kept, "HIDMaestro never recorded its earlier value"),
                Entry("removal step", "running programs", HMUninstallOutcome.Failed, "could not be read: Access is denied")));
            Assert.Equal(3, rows.Count);
            Assert.Equal("oem5.inf (hidmaestro.inf)", rows[0].Location);
            Assert.Equal("driver package", rows[0].Kind);
            Assert.Null(rows[0].Detail);
            Assert.Equal(string.Format(Strings.Instance.HIDMaestroUninstall_Stays_Format, "HIDMaestro never recorded its earlier value"), rows[1].Detail);
            Assert.Equal(string.Format(Strings.Instance.HIDMaestroUninstall_CannotRead_Format, "could not be read: Access is denied"), rows[2].Detail);

            // Each row leads with what the item is, its first letter upper,
            // and ends with what becomes of it. Only an item the removal
            // takes reads quiet.
            Assert.Equal("Driver package", rows[0].Title);
            Assert.Equal("Service setting", rows[1].Title);
            Assert.Equal(Strings.Instance.HIDMaestroUninstall_OutcomeRemoved, rows[0].Outcome);
            Assert.Equal(Strings.Instance.HIDMaestroUninstall_OutcomeKept, rows[1].Outcome);
            Assert.Equal(Strings.Instance.HIDMaestroUninstall_OutcomeUnread, rows[2].Outcome);
            Assert.Equal(new[] { true, false, false }, rows.Select(r => r.IsRemoved));
            Assert.Equal("Driver package, oem5.inf (hidmaestro.inf), " + Strings.Instance.HIDMaestroUninstall_OutcomeRemoved, rows[0].ToString());
        }

        /// <summary>After a removal the list holds only what it could not
        /// take, each with how it failed.</summary>
        [Fact]
        public void ARemovalListsOnlyWhatItCouldNotTake()
        {
            var rows = UninstallHIDMaestroDialog.RowsFor(Report(false, HMUninstallStatus.Failed, Array.Empty<HMRunningProgram>(),
                Entry("devnode", @"ROOT\HIDCLASS\0000", HMUninstallOutcome.Removed),
                Entry("folder", @"C:\Program Files\HIDMaestro", HMUninstallOutcome.RemovedAtRestart),
                Entry("service setting", "GameInputSvc", HMUninstallOutcome.Kept, "HIDMaestro never recorded its earlier value"),
                Entry("OpenVR registration", @"C:\Users\Player\AppData\Local\openvr\openvrpaths.vrpath", HMUninstallOutcome.Failed, "in use (Win32 error 32)")));
            var row = Assert.Single(rows);
            Assert.Equal("OpenVR registration", row.Kind);
            Assert.Equal(string.Format(Strings.Instance.HIDMaestroUninstall_NotRemoved_Format, "in use (Win32 error 32)"), row.Detail);
            Assert.Equal("OpenVR registration", row.Title);
            Assert.Equal(Strings.Instance.HIDMaestroUninstall_OutcomeNotRemoved, row.Outcome);
            Assert.False(row.IsRemoved);
        }

        /// <summary>Uninstall opens only when a removal has something to
        /// do and nothing blocks it. A part of the PC the preview could not
        /// read counts, since the removal reads it again.</summary>
        [Fact]
        public void UninstallOpensOnlyWhenThereIsSomethingToTake()
        {
            var none = Array.Empty<HMRunningProgram>();
            var take = Entry("driver package", "oem5.inf (hidmaestro.inf)", HMUninstallOutcome.WouldRemove);
            var keep = Entry("service setting", "GameInputSvc", HMUninstallOutcome.Kept, "HIDMaestro never recorded its earlier value");
            var unread = Entry("folder", @"C:\Program Files\HIDMaestro", HMUninstallOutcome.Failed, "could not be opened");

            Assert.True(UninstallHIDMaestroDialog.CanRemove(Report(true, HMUninstallStatus.Previewed, none, take, keep)));
            Assert.True(UninstallHIDMaestroDialog.CanRemove(Report(true, HMUninstallStatus.Failed, none, unread)));
            Assert.False(UninstallHIDMaestroDialog.CanRemove(Report(true, HMUninstallStatus.Previewed, none, keep)));
            Assert.False(UninstallHIDMaestroDialog.CanRemove(Report(true, HMUninstallStatus.Previewed, none)));
            Assert.False(UninstallHIDMaestroDialog.CanRemove(Report(true, HMUninstallStatus.Blocked,
                new[] { Program(4528, @"C:\Tools\DS4Windows.exe") }, take)));
            Assert.False(UninstallHIDMaestroDialog.CanRemove(null));
        }

        /// <summary>The Settings flow confirms first, shows the engine
        /// stopping, waits out a stop the Dashboard toggle started, and only
        /// then runs the removal with the engine's stop. Nothing in it starts
        /// the engine again: starting it installs HIDMaestro again.</summary>
        [Fact]
        public void TheFlowStopsTheEngineFirstAndNeverStartsIt()
        {
            string main = OffLimitsSource.Read("PadForge.App/MainWindow.xaml.cs");
            Assert.Contains("_viewModel.Settings.UninstallHIDMaestroRequested += async (s, e) => await UninstallHIDMaestroAsync();",
                main, StringComparison.Ordinal);
            string body = FenceSource.Body(main, "private async Task UninstallHIDMaestroAsync()");
            int confirm = body.IndexOf("Views.UninstallHIDMaestroDialog.Confirm(", StringComparison.Ordinal);
            int stopping = body.IndexOf("if (stopEngine) ShowEngineStopping();", StringComparison.Ordinal);
            int wait = body.IndexOf("try { pendingStop?.Wait(); } catch { }", StringComparison.Ordinal);
            int run = body.IndexOf("report = HIDMaestroRemoval.Run(includeUsbip, stopEngine ? _inputService.Stop : null);", StringComparison.Ordinal);
            Assert.True(confirm >= 0 && stopping > confirm && wait > stopping && run > wait,
                $"confirm {confirm}, stopping {stopping}, wait {wait}, run {run}");
            Assert.DoesNotContain("_inputService.Start", body, StringComparison.Ordinal);
            Assert.Contains("_engineStopTask = System.Threading.Tasks.Task.Run(() => _inputService.Stop());", main, StringComparison.Ordinal);
        }

        /// <summary>The Settings card reads the driver store, never an
        /// HIDMaestro context: a context's warm-up stages the driver files
        /// again in an elevated process with no driver installed, which
        /// would follow every removal.</summary>
        [Fact]
        public void TheSettingsCardReadsTheDriverStoreAndNoContext()
        {
            string removal = OffLimitsSource.Read("PadForge.App/Services/HIDMaestroRemoval.cs");
            Assert.DoesNotContain("new HMContext(", removal, StringComparison.Ordinal);
            Assert.Contains("Directory.EnumerateDirectories(repository, \"hidmaestro.inf_*\")", removal, StringComparison.Ordinal);
            string main = OffLimitsSource.Read("PadForge.App/MainWindow.xaml.cs");
            Assert.Contains("=> _viewModel.Settings.IsHIDMaestroInstalled = HIDMaestroRemoval.IsDriverInstalled();", main, StringComparison.Ordinal);

            // Whatever this PC holds, the read answers and never throws.
            bool installed = HIDMaestroRemoval.IsDriverInstalled();
            Assert.Equal(installed, HIDMaestroRemoval.IsDriverInstalled());
        }

        /// <summary>The card shows the installed state in the UI language
        /// and follows a change of it.</summary>
        [Fact]
        public void TheCardStatusFollowsTheDriverStore()
        {
            var vm = new PadForge.ViewModels.SettingsViewModel();
            var raised = new List<string>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            vm.IsHIDMaestroInstalled = true;
            Assert.Equal(Strings.Instance.Common_Installed, vm.HIDMaestroStatusText);
            Assert.Contains(nameof(vm.HIDMaestroStatusText), raised);
            vm.IsHIDMaestroInstalled = false;
            Assert.Equal(Strings.Instance.Common_NotInstalled, vm.HIDMaestroStatusText);
            Assert.True(vm.UninstallHIDMaestroCommand.CanExecute(null));
        }
    }
}
