using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.Views;
using Xunit;
using static PadForge.Services.BthPS3Removal;

namespace PadForge.Tests
{
    /// <summary>
    /// Removing BthPS3 from Settings: BthPS3's own setup's removal steps, in
    /// its order, then the services, files, WinUSB package and certificate
    /// that removal leaves. The engine stops first. BthPS3 stays while
    /// DsHidMini needs it, and a removal is held while a PlayStation
    /// controller is connected over Bluetooth. The test host never runs the
    /// steps (TestHostFenceTests), so these tests hand the removal a read of
    /// a PC and a record of its steps.
    /// </summary>
    public class BthPS3RemovalTests
    {
        private static Ds3DriverInstaller.DriverPackage Package(string published, string inf, string provider = "Nefarius Software Solutions e.U.", string signer = "Nefarius Software Solutions e.U.")
            => new(inf + "_amd64_0000000000000000", published, inf, provider, signer);

        private static readonly Ds3DriverInstaller.DriverPackage Profile = Package("oem10.inf", "bthps3.inf");
        private static readonly Ds3DriverInstaller.DriverPackage Null = Package("oem11.inf", "bthps3_pdo_null_device.inf");
        private static readonly Ds3DriverInstaller.DriverPackage Filter = Package("oem12.inf", "bthps3psm.inf");
        private static readonly Ds3DriverInstaller.DriverPackage WinUsb = Package("oem98.inf", "ds3_winusb.inf", "PadForge", "PadForge DS3 WinUSB");
        private static readonly Ds3DriverInstaller.DriverPackage VendorUsb = Package("oem99.inf", "padforge_vendorusb.inf", "PadForge", "PadForge DS3 WinUSB");

        private static readonly string[] Files = DriverFilePaths().ToArray();

        /// <summary>A PC where PadForge installed BthPS3 and paired a pad.</summary>
        private static MachineState Installed(bool dsHidMini = false, bool probed = true, int connected = 0,
            string setup = null, params Ds3DriverInstaller.DriverPackage[] extra)
            => new(setup, setup != null ? "Nefarius BthPS3 Bluetooth Drivers 3.2.1" : null, true,
                new[] { "BthPS3", "BthPS3PSM" }, Files,
                new[] { Profile, Null, Filter, WinUsb }.Concat(extra).ToList(),
                true, dsHidMini, probed, connected);

        private static readonly MachineState Clean = new(null, null, false, Array.Empty<string>(), Array.Empty<string>(),
            Array.Empty<Ds3DriverInstaller.DriverPackage>(), false, false, true, 0);

        /// <summary>Each step as it is asked for, with what it answers.</summary>
        private sealed class Recorder : ISteps
        {
            public readonly List<string> Calls = new();
            public bool RadioOn = true;
            public bool RadioComesBack = true;
            public int SetupExit;
            public Func<string, bool> ServicePending = _ => false;
            public Func<string, bool> FilePending = _ => false;
            public Action<string> Throws = _ => { };
            public Action<string> AfterStep = _ => { };

            private void Did(string call)
            {
                Calls.Add(call);
                Throws(call);
                AfterStep(call);
            }

            public int UninstallSetup(string productCode, Action<string> log) { Did("setup " + productCode); return SetupExit; }
            public void RemoveClassFilter(Action<string> log) => Did("filter");
            public bool DisableService(Action<string> log) { Did("service off"); return true; }
            public bool SetRadioOn(bool on, Action<string> log) { Did(on ? "radio on" : "radio off"); RadioOn = on; return true; }
            public void RestartOffRadio(Action<string> log) => Did("restart");
            public bool RadioIsOn(int timeoutMs)
            {
                Calls.Add($"radio? {timeoutMs}");
                return timeoutMs == 0 ? RadioOn : RadioOn && RadioComesBack;
            }
            public void RemoveBthPs3Package(Ds3DriverInstaller.DriverPackage package, Action<string> log) => Did("package " + package.Published);
            public bool DeleteService(string name, Action<string> log) { Did("delete " + name); return ServicePending(name); }
            public bool DeleteDriverFile(string path, Action<string> log) { Did("file " + Path.GetFileName(path)); return FilePending(path); }
            public void DeletePadForgePackage(Ds3DriverInstaller.DriverPackage package, Action<string> log) => Did("winusb " + package.Published);
            public void RemoveCertificate(Action<string> log) => Did("certificate");
        }

        /// <summary>Runs a removal against reads that return
        /// <paramref name="before"/> until <paramref name="switchAfter"/>
        /// has been called, and <paramref name="after"/> from then on.</summary>
        private static Report RunWith(Recorder steps, MachineState before, MachineState after,
            Action stopEngine = null, string switchAfter = null)
        {
            bool done = switchAfter == null;
            steps.AfterStep = call => { if (call == switchAfter) done = true; };
            int reads = 0;
            ReadSeam = () =>
            {
                reads++;
                // The first read is the plan. Without a switch, every later
                // read is the PC after the steps.
                return switchAfter == null ? (reads == 1 ? before : after) : (done ? after : before);
            };
            StepsSeam = steps;
            try { return Run(stopEngine); }
            finally { ReadSeam = null; StepsSeam = null; }
        }

        [Fact]
        public void APadForgeInstall_PreviewsEveryPart()
        {
            var report = Plan(Installed());
            Assert.True(report.IsPreview);
            Assert.Equal(RemovalStatus.Previewed, report.Status);
            Assert.Equal(Blocker.None, report.Blocker);
            Assert.All(report.Items, i => Assert.Equal(ItemOutcome.WouldRemove, i.Outcome));
            Assert.Equal(new[]
            {
                (ItemKind.ClassFilter, "BthPS3PSM"),
                (ItemKind.Service, "BthPS3"), (ItemKind.Service, "BthPS3PSM"),
                (ItemKind.DriverPackage, "oem10.inf (bthps3.inf)"),
                (ItemKind.DriverPackage, "oem11.inf (bthps3_pdo_null_device.inf)"),
                (ItemKind.DriverPackage, "oem12.inf (bthps3psm.inf)"),
                (ItemKind.DriverFile, Files[0]), (ItemKind.DriverFile, Files[1]),
                (ItemKind.WinUsbPackage, "oem98.inf (ds3_winusb.inf)"),
                (ItemKind.Certificate, "PadForge DS3 WinUSB"),
            }, report.Items.Select(i => (i.Kind, i.Location)));
            Assert.Null(UninstallBthPS3Dialog.NoteFor(report));
            Assert.True(UninstallBthPS3Dialog.CanRemove(report));
        }

        [Fact]
        public void TheBinaries_AreTheOnesTheInfsCopyToTheDriversFolder()
            => Assert.Equal(new[]
            {
                Path.Combine(Environment.SystemDirectory, "drivers", "BthPS3.sys"),
                Path.Combine(Environment.SystemDirectory, "drivers", "BthPS3PSM.sys"),
            }, Files);

        /// <summary>The certificate signs PadForge's other WinUSB packages too,
        /// so it stays while one is in the store. Another provider's
        /// ds3_winusb.inf is not PadForge's to take.</summary>
        [Fact]
        public void TheCertificate_StaysWhileItSignsAnotherPackage()
        {
            var report = Plan(Installed(extra: new[] { VendorUsb }));
            var cert = Assert.Single(report.Items, i => i.Kind == ItemKind.Certificate);
            Assert.Equal(ItemOutcome.Kept, cert.Outcome);
            Assert.Equal(Strings.Instance.BthPS3Uninstall_CertificateKept, cert.Reason);
            Assert.DoesNotContain(report.Items, i => i.Location.Contains("padforge_vendorusb"));

            var foreign = Plan(Clean with { Packages = new[] { Package("oem50.inf", "ds3_winusb.inf", "Someone Else", "Someone Else") } });
            Assert.Equal(RemovalStatus.Nothing, foreign.Status);
            Assert.Empty(foreign.Items);
        }

        /// <summary>DsHidMini needs BthPS3, so BthPS3 stays and says why. What
        /// PadForge added for its own WinUSB lane still goes.</summary>
        [Fact]
        public void BthPS3_StaysWhileDsHidMiniIsInstalled()
        {
            var report = Plan(Installed(dsHidMini: true, connected: 1));
            Assert.Equal(RemovalStatus.Previewed, report.Status);
            Assert.Equal(Blocker.None, report.Blocker);
            Assert.All(report.Items.Where(i => i.IsStack), i =>
            {
                Assert.Equal(ItemOutcome.Kept, i.Outcome);
                Assert.Equal(Strings.Instance.BthPS3Uninstall_DsHidMini, i.Reason);
            });
            Assert.All(report.Items.Where(i => !i.IsStack), i => Assert.Equal(ItemOutcome.WouldRemove, i.Outcome));

            var stackOnly = Plan(Installed(dsHidMini: true) with
            {
                Packages = new[] { Profile, Null, Filter },
                Certificate = false,
            });
            Assert.Equal(RemovalStatus.Nothing, stackOnly.Status);
            Assert.Equal((Strings.Instance.BthPS3Uninstall_DsHidMini, false), UninstallBthPS3Dialog.NoteFor(stackOnly));
            Assert.False(UninstallBthPS3Dialog.CanRemove(stackOnly));
        }

        /// <summary>The removal restarts the radio, which tears down a
        /// connected PlayStation controller, and BthPS3 2.12.0 to 3.2.0 crash
        /// Windows when that happens (nefarius/BthPS3#182). A check that
        /// could not run holds it too. With BthPS3 itself gone, nothing
        /// restarts the radio and nothing holds the rest.</summary>
        [Fact]
        public void AConnectedController_HoldsTheRemoval()
        {
            var connected = Plan(Installed(connected: 1));
            Assert.Equal(RemovalStatus.Blocked, connected.Status);
            Assert.Equal(Blocker.ControllerConnected, connected.Blocker);
            Assert.Equal((Strings.Instance.BthPS3Uninstall_Connected, true), UninstallBthPS3Dialog.NoteFor(connected));
            Assert.False(UninstallBthPS3Dialog.CanRemove(connected));

            var unchecked_ = Plan(Installed(probed: false));
            Assert.Equal(Blocker.NotChecked, unchecked_.Blocker);
            Assert.Equal((Strings.Instance.BthPS3Uninstall_NotChecked, true), UninstallBthPS3Dialog.NoteFor(unchecked_));

            var leftovers = Plan(Clean with { Packages = new[] { WinUsb }, Certificate = true, Probed = false, Connected = 2 });
            Assert.Equal(RemovalStatus.Previewed, leftovers.Status);
            Assert.Equal(Blocker.None, leftovers.Blocker);
        }

        [Fact]
        public void APcWithoutBthPS3_HasNothingToRemove()
        {
            var report = Plan(Clean);
            Assert.Equal(RemovalStatus.Nothing, report.Status);
            Assert.Empty(report.Items);
            Assert.Equal((Strings.Instance.BthPS3Uninstall_Nothing, false), UninstallBthPS3Dialog.NoteFor(report));
            Assert.False(UninstallBthPS3Dialog.CanRemove(report));
        }

        /// <summary>BthPS3's own order (CustomActions.cs UninstallDrivers):
        /// the filter's registration, the service withdrawn, the radio off,
        /// the radio device restarted, the packages, the radio on. Then what
        /// that removal leaves. The engine stops before any of it.</summary>
        [Fact]
        public void TheSteps_RunInBthPS3sOwnOrder_AfterTheEngineStops()
        {
            var steps = new Recorder();
            var report = RunWith(steps, Installed(), Clean, () => steps.Calls.Add("stop engine"));
            Assert.Equal(new[]
            {
                "stop engine", "filter", "radio? 0", "service off", "radio off", "restart",
                "package oem10.inf", "package oem11.inf", "package oem12.inf",
                "radio on", "radio? 20000",
                "delete BthPS3", "delete BthPS3PSM", "file BthPS3.sys", "file BthPS3PSM.sys",
                "winusb oem98.inf", "certificate",
            }, steps.Calls);
            Assert.False(report.IsPreview);
            Assert.Equal(RemovalStatus.Removed, report.Status);
            Assert.All(report.Items, i => Assert.Equal(ItemOutcome.Removed, i.Outcome));
            Assert.Equal(Strings.Instance.Status_BthPS3Removed, StatusLine(report));
        }

        /// <summary>The service is withdrawn through a radio that is on, so
        /// an off radio is switched on for it, and is off again
        /// afterward.</summary>
        [Fact]
        public void ARadioTheUserSwitchedOff_IsOffAgainAfterward()
        {
            var steps = new Recorder { RadioOn = false };
            var report = RunWith(steps, Installed(), Clean);
            Assert.Equal(new[]
            {
                "filter", "radio? 0", "radio on", "radio? 20000", "service off", "radio off", "restart",
                "package oem10.inf", "package oem11.inf", "package oem12.inf",
                "delete BthPS3", "delete BthPS3PSM", "file BthPS3.sys", "file BthPS3PSM.sys",
                "winusb oem98.inf", "certificate",
            }, steps.Calls);
            Assert.False(steps.RadioOn);
            Assert.Equal(RemovalStatus.Removed, report.Status);
        }

        /// <summary>BthPS3's own setup removes what it installed first. The
        /// steps after it act on what the PC holds then: here the services
        /// and binaries it leaves, which need no radio restart.</summary>
        [Fact]
        public void BthPS3sOwnSetup_RemovesItFirst()
        {
            var steps = new Recorder();
            var leftBySetup = Clean with { Services = new[] { "BthPS3", "BthPS3PSM" }, DriverFiles = Files, Packages = new[] { WinUsb }, Certificate = true };
            var report = RunWith(steps, Installed(setup: "{CC32A6ED-0000-0000-0000-000000000000}"), Clean
                , switchAfter: "setup {CC32A6ED-0000-0000-0000-000000000000}");
            // The read after the setup's removal is the state that removal
            // left, until the steps after it change it.
            Assert.Equal("setup {CC32A6ED-0000-0000-0000-000000000000}", steps.Calls[0]);
            Assert.DoesNotContain("restart", steps.Calls);
            Assert.DoesNotContain(steps.Calls, c => c.StartsWith("package", StringComparison.Ordinal));
            Assert.Equal(RemovalStatus.Removed, report.Status);
            Assert.Contains(report.Items, i => i.Kind == ItemKind.Setup && i.Outcome == ItemOutcome.Removed);

            // With the leftovers in place, they go.
            steps = new Recorder();
            int reads = 0;
            ReadSeam = () => ++reads switch
            {
                1 => Installed(setup: "{CC32A6ED-0000-0000-0000-000000000000}"),
                2 => leftBySetup,
                _ => Clean,
            };
            StepsSeam = steps;
            try { report = Run(null); }
            finally { ReadSeam = null; StepsSeam = null; }
            Assert.Equal(new[]
            {
                "setup {CC32A6ED-0000-0000-0000-000000000000}",
                "delete BthPS3", "delete BthPS3PSM", "file BthPS3.sys", "file BthPS3PSM.sys",
                "winusb oem98.inf", "certificate",
            }, steps.Calls);
            Assert.Equal(RemovalStatus.Removed, report.Status);
        }

        /// <summary>Exit codes of the setup's removal: 3010 means Windows
        /// finishes it at its next start, 1605 that it was gone already,
        /// anything else a failure.</summary>
        [Theory]
        [InlineData(0, "Removed")]
        [InlineData(1605, "Removed")]
        [InlineData(3010, "RemovedRestartPending")]
        [InlineData(1603, "Failed")]
        [InlineData(-1, "Failed")]
        public void TheSetupsExitCode_DecidesItsOutcome(int exit, string expected)
        {
            var status = Enum.Parse<RemovalStatus>(expected);
            var steps = new Recorder { SetupExit = exit };
            var after = status == RemovalStatus.Failed
                ? Clean with { SetupProductCode = "{X}", SetupName = "Nefarius BthPS3 Bluetooth Drivers 3.2.1" }
                : Clean;
            var report = RunWith(steps, Installed(setup: "{X}"), after, switchAfter: "setup {X}");
            Assert.Equal(status, report.Status);
            var setup = Assert.Single(report.Items, i => i.Kind == ItemKind.Setup);
            Assert.Equal(status == RemovalStatus.Failed ? ItemOutcome.Failed : ItemOutcome.Removed, setup.Outcome);
        }

        /// <summary>A service still loaded is deleted at the next start, and
        /// so is a binary still in use: the removal counts both as removed
        /// and says Windows finishes it.</summary>
        [Fact]
        public void WhatWindowsDeletesAtItsNextStart_CountsAsRemoved()
        {
            var steps = new Recorder { FilePending = p => p.EndsWith("BthPS3PSM.sys", StringComparison.OrdinalIgnoreCase) };
            var after = Clean with { DriverFiles = new[] { Files[1] } };
            var report = RunWith(steps, Installed(), after);
            Assert.Equal(RemovalStatus.RemovedRestartPending, report.Status);
            Assert.All(report.Items, i => Assert.Equal(ItemOutcome.Removed, i.Outcome));
            Assert.Equal(Strings.Instance.Status_BthPS3RemovedRestart, StatusLine(report));

            steps = new Recorder { ServicePending = n => n == "BthPS3" };
            report = RunWith(steps, Installed(), Clean);
            Assert.Equal(RemovalStatus.RemovedRestartPending, report.Status);
        }

        /// <summary>A step that throws, or an item still there afterward,
        /// fails with its reason. The report then lists only what
        /// failed.</summary>
        [Fact]
        public void WhatStays_FailsWithItsReason()
        {
            var steps = new Recorder
            {
                Throws = call => { if (call == "package oem11.inf") throw new InvalidOperationException("in use"); },
            };
            var after = Clean with { Packages = new[] { Null }, Services = new[] { "BthPS3" } };
            var report = RunWith(steps, Installed(), after);
            Assert.Equal(RemovalStatus.Failed, report.Status);
            var package = Assert.Single(report.Items, i => i.Location == "oem11.inf (bthps3_pdo_null_device.inf)");
            Assert.Equal((ItemOutcome.Failed, "in use"), (package.Outcome, package.Reason));
            var service = Assert.Single(report.Items, i => i.Kind == ItemKind.Service && i.Location == "BthPS3");
            Assert.Equal((ItemOutcome.Failed, Strings.Instance.BthPS3Uninstall_StillThere), (service.Outcome, service.Reason));
            Assert.Equal(Strings.Instance.Status_BthPS3RemovalFailed, StatusLine(report));

            var rows = UninstallBthPS3Dialog.RowsFor(report);
            Assert.Equal(2, rows.Count);
            Assert.Contains(rows, r => r.Detail == string.Format(Strings.Instance.HIDMaestroUninstall_NotRemoved_Format, "in use")
                && r.Kind == Strings.Instance.BthPS3Uninstall_KindPackage);
            // After a removal each row says the item was not removed.
            Assert.All(rows, r =>
            {
                Assert.Equal(Strings.Instance.HIDMaestroUninstall_OutcomeNotRemoved, r.Outcome);
                Assert.False(r.IsRemoved);
            });
        }

        /// <summary>A radio that does not come back on is a failure of its own,
        /// with what to do about it.</summary>
        [Fact]
        public void ARadioThatDoesNotComeBack_IsReported()
        {
            var steps = new Recorder { RadioComesBack = false };
            var report = RunWith(steps, Installed(), Clean);
            Assert.Equal(RemovalStatus.Failed, report.Status);
            var radio = Assert.Single(report.Items, i => i.Kind == ItemKind.Radio);
            Assert.Equal((ItemOutcome.Failed, Strings.Instance.BthPS3Uninstall_RadioOff), (radio.Outcome, radio.Reason));
            Assert.Equal(Strings.Instance.BthPS3Uninstall_KindRadio, KindText(ItemKind.Radio));
        }

        /// <summary>A controller that connected after the preview holds the
        /// removal: no step runs, and the status line says why.</summary>
        [Fact]
        public void AControllerConnectedSinceThePreview_HoldsTheRun()
        {
            var steps = new Recorder();
            var report = RunWith(steps, Installed(connected: 1), Installed(connected: 1), () => steps.Calls.Add("stop engine"));
            Assert.Equal(new[] { "stop engine" }, steps.Calls);
            Assert.False(report.IsPreview);
            Assert.Equal(RemovalStatus.Blocked, report.Status);
            Assert.Equal(string.Format(Strings.Instance.Status_BthPS3RemovalBlocked_Format, Strings.Instance.BthPS3Uninstall_Connected),
                StatusLine(report));
        }

        /// <summary>A preview lists every item, with the reason beside each
        /// one that stays.</summary>
        [Fact]
        public void ThePreviewRows_NameEachItemAndWhyOneStays()
        {
            var rows = UninstallBthPS3Dialog.RowsFor(Plan(Installed(extra: new[] { VendorUsb })));
            Assert.Equal(10, rows.Count);
            var cert = Assert.Single(rows, r => r.Kind == Strings.Instance.BthPS3Uninstall_KindCertificate);
            Assert.Equal(string.Format(Strings.Instance.HIDMaestroUninstall_Stays_Format, Strings.Instance.BthPS3Uninstall_CertificateKept), cert.Detail);
            Assert.All(rows.Where(r => r != cert), r => Assert.Null(r.Detail));
            // Each row leads with what the item is and ends with what
            // becomes of it, in the HIDMaestro removal's words.
            Assert.Equal(Strings.Instance.BthPS3Uninstall_KindCertificate, cert.Title);
            Assert.Equal(Strings.Instance.HIDMaestroUninstall_OutcomeKept, cert.Outcome);
            Assert.False(cert.IsRemoved);
            Assert.All(rows.Where(r => r != cert), r =>
            {
                Assert.Equal(Strings.Instance.HIDMaestroUninstall_OutcomeRemoved, r.Outcome);
                Assert.True(r.IsRemoved);
            });
            Assert.Equal(Enum.GetValues<ItemKind>().Length, Enum.GetValues<ItemKind>().Select(KindText).Distinct().Count());
        }
    }
}
