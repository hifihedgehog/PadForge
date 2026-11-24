using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PadForge.Common
{
    /// <summary>Why an ARM64 HidHide install or removal stopped short.</summary>
    public enum HidHideSetupFailure
    {
        /// <summary>The driver installed and its control device never
        /// opened, so no filter was added and the device's removal was
        /// attempted.</summary>
        DriverDidNotStart,

        /// <summary>A class still lists HidHide after the removal, so the
        /// driver was left in place.</summary>
        FilterStillListed,
    }

    /// <summary>A stop the installer chose. It is shown through the same
    /// "Driver operation failed" line every other installer fault uses.</summary>
    public sealed class HidHideSetupException : Exception
    {
        public HidHideSetupFailure Failure { get; }

        public HidHideSetupException(HidHideSetupFailure failure)
            : base(failure == HidHideSetupFailure.DriverDidNotStart
                ? "HidHide installed but its control device did not open, so no class filter was added."
                : "HidHide is still listed as a class filter, so the driver was left in place.")
        {
            Failure = failure;
        }
    }

    /// <summary>
    /// Installs and removes HidHide on Windows ARM64, where upstream's MSI does
    /// not apply.
    ///
    /// <para>Upstream publishes a Microsoft-signed ARM64 driver package
    /// (<c>drivers/HidHide_ARM64.zip</c>, driver 1.6.280.0) and documents the
    /// manual install at
    /// docs.nefarius.at/projects/HidHide/Manual-Installation-ARM64. This class
    /// performs those documented steps with upstream's own tool, nefcon, in
    /// the order upstream's installer uses (HidHide <c>Installer/Program.cs</c>:
    /// OnAfterInstall, and OnBeforeInstall when it is uninstalling): the
    /// device and driver first, then the three class filters, and on removal
    /// the filters first, then the device.</para>
    ///
    /// <para>nefconc.exe is run as a child process rather than making the
    /// SetupAPI calls from this process. The bundled one is a native ARM64
    /// image, so the install is performed by native code whether PadForge
    /// itself is the ARM64 build or the x64 build running emulated. Microsoft
    /// documents that emulation covers user mode only and says nothing about
    /// driver installation from an emulated caller, and upstream's own ARM64
    /// setup and libwdi both hand the job to a native helper for that
    /// reason.</para>
    ///
    /// <para>One step here is not in the manual procedure. A class filter
    /// entry that names a driver which is not there "can prevent the whole
    /// device class from starting" (nefcon README), and for HIDClass that
    /// class is every keyboard and mouse. Upstream's x64 setup ships a
    /// watchdog service that removes such entries. The manual procedure has
    /// none, so the filters are added here only after the driver's control
    /// device has opened, and <see cref="RemoveDanglingFilters"/> is the
    /// watchdog's check, run at startup.</para>
    /// </summary>
    internal static class HidHideArm64Installer
    {
        internal const string DriverPackageResource = "HidHide_ARM64.zip";
        internal const string NefconResource = "nefconc.exe";
        internal const string HardwareId = @"root\HidHide";
        internal const string ServiceName = "HidHide";

        /// <summary>The three device setup classes HidHide filters, in the
        /// order upstream adds them. HIDClass is the one that matters for a
        /// controller. The other two exist for Xbox 360 era hardware and may
        /// be absent on a given machine, which is why upstream treats a
        /// failure there as ignorable.</summary>
        internal static readonly Guid HidClass = new Guid("745a17a0-74d3-11d0-b6fe-00a0c90f57da");
        internal static readonly Guid XnaCompositeClass = new Guid("d61ca365-5af4-4486-998b-9db4734c6ca3");
        internal static readonly Guid XboxCompositeClass = new Guid("05f5cfe2-4733-4950-a6bb-07aad01a3a84");

        internal static readonly Guid[] FilteredClasses = { HidClass, XnaCompositeClass, XboxCompositeClass };

        /// <summary>One install, removal or startup check at a time. The
        /// startup check runs on its own thread, and without this a check
        /// that had already listed what to remove could take a filter back
        /// out after an install had just put it in. It covers this process
        /// alone: another installer, or a nefcon that outlived its wait, is
        /// outside it.</summary>
        private static readonly object OperationLock = new object();

        // ── nefcon command lines, exactly as upstream writes them ──

        internal static string InstallArgs(string infPath)
            => $"install \"{infPath}\" \"{HardwareId}\" --no-duplicates --remove-duplicates";

        internal static string RemoveDeviceArgs()
            => $"remove \"{HardwareId}\"";

        internal static string AddFilterArgs(Guid deviceClass)
            => $"--add-class-filter --position upper --service-name {ServiceName} --class-guid {deviceClass:D}";

        internal static string RemoveFilterArgs(Guid deviceClass)
            => $"--remove-class-filter --position upper --service-name {ServiceName} --class-guid {deviceClass:D}";

        /// <summary>nefcon answers 0, or 3010 when Windows wants a restart to
        /// finish (NefConUtil.cpp returns ERROR_SUCCESS_REBOOT_REQUIRED from
        /// every command that can need one). An install also answers 259,
        /// ERROR_NO_MORE_ITEMS, when the driver already on the device is as
        /// good as the one offered, which is how
        /// UpdateDriverForPlugAndPlayDevices reports "nothing to do".</summary>
        internal static bool IsSuccess(int exitCode, bool installing)
            => exitCode == 0 || exitCode == 3010 || (installing && exitCode == 259);

        /// <summary>Runs one nefcon command and returns its exit code, or null
        /// when it was still running after the wait.</summary>
        internal delegate int? NefconRunner(string arguments);

        // ── Install ──

        /// <summary>
        /// The install sequence over seams, so its order and its refusals can
        /// be tested without a driver.
        /// </summary>
        /// <param name="run">Runs nefcon.</param>
        /// <param name="infPath">The extracted HidHide.inf.</param>
        /// <param name="driverAnswers">Whether HidHide's control device opens.
        /// Asked after the device is installed and before any filter is
        /// added.</param>
        internal static void Install(NefconRunner run, string infPath, Func<bool> driverAnswers)
        {
            int? installed = run(InstallArgs(infPath));
            Require(installed, installing: true);

            // The gate. With the driver not answering, a HIDClass filter entry
            // would name a driver Windows cannot start, and every HID device
            // on the machine would fail to start with it.
            if (!driverAnswers())
            {
                // Windows itself answered 3010: it will not start this
                // driver until a restart. That is reported as what it is, the
                // installer's own exit code, through the line every installer
                // fault uses. The device stays in and no filter is added, so
                // Install stays available and finishes the job afterward.
                if (installed == 3010) throw new InstallerFailedException(3010);

                // No restart was asked for and the driver still does not
                // answer. Add nothing, and take the device back out if it
                // will come. Whether it came is not checked, and a removal
                // that throws is swallowed: the fault to report is the
                // driver's, and a device left in without a filter still
                // reads as not installed, with Install offered.
                try { run(RemoveDeviceArgs()); } catch { }
                throw new HidHideSetupException(HidHideSetupFailure.DriverDidNotStart);
            }

            // HIDClass has to take. The Xbox 360 era classes are best effort,
            // as they are in upstream's installer.
            Require(run(AddFilterArgs(HidClass)), installing: false);
            run(AddFilterArgs(XnaCompositeClass));
            run(AddFilterArgs(XboxCompositeClass));
        }

        // ── Uninstall ──

        /// <summary>
        /// Removes the class filter entries, checks that HidHide is no longer
        /// listed, and then removes the root device node, which is upstream's
        /// order. The service and the driver package stay, as they do after
        /// upstream's own uninstall: nefcon's <c>remove</c> is DIF_REMOVE on
        /// the device node and nothing more.
        ///
        /// <para>A removal that stops part way, by an exit code or by a
        /// throw, tries to put back what it took off. Left alone, the
        /// filters would be gone and the device still there, which reads as
        /// "not installed" and takes the Uninstall button away from the
        /// person who just asked for it. So the filters that were listed at
        /// the start are added again and the failure is reported. When every
        /// add succeeds the state is a complete install once more and
        /// Uninstall can be tried again, which is how a failed MSI uninstall
        /// behaves. When one does not, the log says which.</para>
        ///
        /// <para>Three things forbid putting anything back. A command that
        /// outlived its wait, because that removal may still be running. A
        /// device node that is gone or cannot be read back, because there is
        /// then no install to complete. And a driver that does not answer,
        /// which is Install's own gate.</para>
        /// </summary>
        /// <param name="filterListed">Whether HidHide is in the class's
        /// UpperFilters, read from the registry.</param>
        /// <param name="deviceNodePresent">Whether the root device node is
        /// there, or null when that could not be read.</param>
        /// <param name="driverAnswers">Whether HidHide's control device
        /// opens, asked before any filter is put back.</param>
        internal static void Uninstall(NefconRunner run, Func<Guid, bool> filterListed,
                                       Func<bool?> deviceNodePresent, Func<bool> driverAnswers,
                                       Action<string> log)
        {
            // Install treats two of the three classes as best effort, so a
            // complete install need not list all three. What is listed now
            // is what a failed removal puts back.
            var listedAtStart = new List<Guid>();
            foreach (Guid deviceClass in FilteredClasses)
                if (filterListed(deviceClass)) listedAtStart.Add(deviceClass);

            bool aCommandMayStillRun = false;
            bool? nodeThere = null;
            bool nodeWasRead = false;
            try
            {
                // Upstream takes them off in the reverse of the order it adds them.
                for (int i = FilteredClasses.Length - 1; i >= 0; i--)
                    if (run(RemoveFilterArgs(FilteredClasses[i])) == null) aCommandMayStillRun = true;

                foreach (Guid deviceClass in FilteredClasses)
                {
                    if (!filterListed(deviceClass)) continue;
                    if (aCommandMayStillRun) throw new InstallerFailedException();
                    throw new HidHideSetupException(HidHideSetupFailure.FilterStillListed);
                }

                int? removed = run(RemoveDeviceArgs());
                if (removed == null)
                {
                    aCommandMayStillRun = true;
                    throw new InstallerFailedException();
                }
                if (IsSuccess(removed.Value, installing: false)) return;

                // nefcon answers 1 for a removal that failed and also for "no
                // device matched" (NefConUtil.cpp, the remove handler), so
                // the node is read back. Gone is done, whatever the exit
                // code said.
                nodeThere = deviceNodePresent();
                nodeWasRead = true;
                if (nodeThere == false) return;
                throw new InstallerFailedException(removed.Value);
            }
            catch
            {
                // Every way the removal can stop comes through here, a
                // command that threw included, and the fault that stopped it
                // is the one the caller hears.
                if (!aCommandMayStillRun)
                {
                    if (!nodeWasRead)
                    {
                        try { nodeThere = deviceNodePresent(); } catch { nodeThere = null; }
                    }
                    if (nodeThere == true) PutBack(run, listedAtStart, filterListed, driverAnswers, log);
                }
                throw;
            }
        }

        /// <summary>Adds back the filters a failed removal took off. It never
        /// throws, because the failure to report is the removal's, and a
        /// second fault here must not take its place.</summary>
        private static void PutBack(NefconRunner run, List<Guid> listedAtStart, Func<Guid, bool> filterListed,
                                    Func<bool> driverAnswers, Action<string> log)
        {
            try
            {
                // The same gate as Install: no filter for a driver that has
                // not been seen running.
                if (!driverAnswers())
                {
                    log("HIDHIDE removal failed and the driver does not answer, so its class filters stay off");
                    return;
                }
                foreach (Guid deviceClass in listedAtStart)
                {
                    if (filterListed(deviceClass)) continue;
                    int? code = run(AddFilterArgs(deviceClass));
                    if (code == null || !IsSuccess(code.Value, installing: false))
                        log("HIDHIDE removal failed and the class filter on " + deviceClass.ToString("D")
                            + " did not go back (" + Describe(code) + ")");
                }
            }
            catch (Exception ex)
            {
                log("HIDHIDE removal failed and putting its class filters back threw: " + ex.Message);
            }
        }

        private static string Describe(int? exitCode)
            => exitCode == null ? "nefcon was still running after the wait" : "nefcon exit code " + exitCode.Value;

        private static void Require(int? exitCode, bool installing)
        {
            if (exitCode == null) throw new InstallerFailedException();
            if (!IsSuccess(exitCode.Value, installing)) throw new InstallerFailedException(exitCode.Value);
        }

        // ── What is on this machine ──

        /// <summary>The driver file the HidHide service names, or null when no
        /// such service is registered. This is what tells an ARM64 install
        /// apart, because nothing registers it with Windows Installer.</summary>
        internal static string RegisteredDriverPath()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\" + ServiceName, false);
                return ExpandServiceImagePath(key?.GetValue("ImagePath") as string);
            }
            catch { return null; }
        }

        /// <summary>A service ImagePath in the forms a kernel driver uses, as
        /// a path File can open: <c>\SystemRoot\System32\drivers\x.sys</c>,
        /// <c>System32\drivers\x.sys</c> or <c>\??\C:\...</c>.</summary>
        internal static string ExpandServiceImagePath(string imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath)) return null;
            string p = imagePath.Trim().Trim('"');
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (p.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
                return Path.Combine(windows, p.Substring(@"\SystemRoot\".Length));
            if (p.StartsWith(@"\??\", StringComparison.Ordinal))
                return p.Substring(@"\??\".Length);
            if (p.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase))
                return Path.Combine(windows, p);
            return Environment.ExpandEnvironmentVariables(p);
        }

        /// <summary>The installed driver's file version, or null.</summary>
        internal static string InstalledDriverVersion()
        {
            try
            {
                string path = RegisteredDriverPath();
                if (path == null || !File.Exists(path)) return null;
                string v = FileVersionInfo.GetVersionInfo(path).FileVersion;
                return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
            }
            catch { return null; }
        }

        /// <summary>Whether HidHide is in a device class's UpperFilters.</summary>
        internal static bool FilterListed(Guid deviceClass)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Class\" + deviceClass.ToString("B"), false);
                return ListsService(key?.GetValue("UpperFilters") as string[]);
            }
            catch { return false; }
        }

        internal static bool ListsService(string[] upperFilters)
        {
            if (upperFilters == null) return false;
            foreach (string entry in upperFilters)
                if (string.Equals(entry, ServiceName, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>What the Service Control Manager says about the HidHide
        /// driver service.</summary>
        internal enum ServiceState
        {
            /// <summary>No such service is registered.</summary>
            Absent,
            /// <summary>Registered, and the driver is not loaded.</summary>
            Stopped,
            /// <summary>The driver is loaded.</summary>
            Running,
            /// <summary>The question could not be answered, or the service is
            /// between two states.</summary>
            Unknown,
        }

        /// <summary>The classes whose filter list names HidHide while its
        /// driver is not there to load: the service is gone, or it is
        /// registered and stopped. A stopped service behind a listed filter
        /// is upstream's watchdog condition ("expecting service to be
        /// running as an indicator that driver is loaded"), and it is what a
        /// driver Windows refuses to load looks like.
        ///
        /// <para>Upstream acts on anything that is not running, from a loop
        /// that runs every five seconds and puts a filter back once the
        /// service runs again. This check runs once and puts nothing back,
        /// so a service between two states is left alone, and so is one the
        /// question could not be asked about, which is what upstream does
        /// with a failed query too.</para></summary>
        internal static List<Guid> DanglingFilters(Func<Guid, bool> filterListed, ServiceState state)
        {
            var dangling = new List<Guid>();
            if (state != ServiceState.Absent && state != ServiceState.Stopped) return dangling;
            foreach (Guid deviceClass in FilteredClasses)
                if (filterListed(deviceClass)) dangling.Add(deviceClass);
            return dangling;
        }

        private const uint SC_MANAGER_CONNECT = 0x0001;
        private const uint SERVICE_QUERY_STATUS = 0x0004;
        private const int SC_STATUS_PROCESS_INFO = 0;
        private const uint SERVICE_STOPPED = 1;
        private const uint SERVICE_RUNNING = 4;
        private const int ERROR_SERVICE_DOES_NOT_EXIST = 1060;

        [StructLayout(LayoutKind.Sequential)]
        private struct SERVICE_STATUS_PROCESS
        {
            public uint dwServiceType;
            public uint dwCurrentState;
            public uint dwControlsAccepted;
            public uint dwWin32ExitCode;
            public uint dwServiceSpecificExitCode;
            public uint dwCheckPoint;
            public uint dwWaitHint;
            public uint dwProcessId;
            public uint dwServiceFlags;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenSCManagerW(string machineName, string databaseName, uint access);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenServiceW(IntPtr scManager, string serviceName, uint access);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool QueryServiceStatusEx(IntPtr service, int infoLevel,
            ref SERVICE_STATUS_PROCESS status, uint bufSize, out uint bytesNeeded);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool CloseServiceHandle(IntPtr handle);

        /// <summary>Asks the Service Control Manager about one service. Only
        /// "no such service" counts as absent. Every other failure is
        /// unknown.</summary>
        internal static ServiceState QueryServiceState(string serviceName)
        {
            IntPtr scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
            if (scm == IntPtr.Zero) return ServiceState.Unknown;
            try
            {
                IntPtr service = OpenServiceW(scm, serviceName, SERVICE_QUERY_STATUS);
                if (service == IntPtr.Zero)
                    return Marshal.GetLastWin32Error() == ERROR_SERVICE_DOES_NOT_EXIST
                        ? ServiceState.Absent
                        : ServiceState.Unknown;
                try
                {
                    var status = new SERVICE_STATUS_PROCESS();
                    if (!QueryServiceStatusEx(service, SC_STATUS_PROCESS_INFO, ref status,
                            (uint)Marshal.SizeOf<SERVICE_STATUS_PROCESS>(), out _))
                        return ServiceState.Unknown;
                    return FromCurrentState(status.dwCurrentState);
                }
                finally { CloseServiceHandle(service); }
            }
            finally { CloseServiceHandle(scm); }
        }

        internal static ServiceState FromCurrentState(uint currentState)
            => currentState == SERVICE_RUNNING ? ServiceState.Running
             : currentState == SERVICE_STOPPED ? ServiceState.Stopped
             : ServiceState.Unknown;

        // ── The real thing ──

        /// <summary>Installs HidHide on this ARM64 machine. As on x64 it asks
        /// for no restart: a controller picks the filter up the next time it
        /// connects, which the Hide from Games tooltip already says.</summary>
        public static void Install()
        {
            lock (OperationLock)
            {
                WithStagedTools((run, staging) =>
                {
                    string driverDir = Path.Combine(staging, "driver");
                    using (var zip = ZipFile.OpenRead(Path.Combine(staging, DriverPackageResource)))
                        zip.ExtractToDirectory(driverDir);

                    string[] inf = Directory.GetFiles(driverDir, "HidHide.inf", SearchOption.AllDirectories);
                    if (inf.Length == 0) throw new FileNotFoundException("HidHide.inf is not in the ARM64 driver package.");

                    Install(run, inf[0], WaitForControlDevice);
                }, needsDriverPackage: true);
            }
        }

        /// <summary>
        /// Whether HidHide is installed the ARM64 way. Nothing registers this
        /// install with Windows Installer, so it is read from what the install
        /// leaves behind: the service, the HIDClass filter and the device
        /// node. All three, because a device with no filter yet is an
        /// install that stopped for a restart, and Install has to stay
        /// available to finish it.
        ///
        /// <para>The two registry reads come first and the device
        /// enumeration last. The status timer asks this every five seconds
        /// on the UI thread, and on a machine without HidHide the registry
        /// says no before any device is enumerated.</para>
        /// </summary>
        public static bool IsInstalled()
            => IsInstalled(() => RegisteredDriverPath() != null,
                           () => FilterListed(HidClass),
                           () => DeviceNodeState() == true);

        internal static bool IsInstalled(Func<bool> serviceRegistered, Func<bool> hidClassFiltered,
                                         Func<bool> deviceNodePresent)
            => serviceRegistered() && hidClassFiltered() && deviceNodePresent();

        private static readonly Guid SystemDeviceClass = new Guid("4D36E97D-E325-11CE-BFC1-08002BE10318");

        /// <summary>Whether the root\HidHide device node is present, or null
        /// when the enumeration itself failed. The two are kept apart because
        /// Uninstall reads "absent" as "the removal is complete".</summary>
        private static bool? DeviceNodeState()
        {
            try
            {
                return Nefarius.Utilities.DeviceManagement.PnP.Devcon.FindInDeviceClassByHardwareId(
                    SystemDeviceClass, HardwareId, out _, true, false);
            }
            catch { return null; }
        }

        /// <summary>Removes HidHide from this ARM64 machine.</summary>
        public static void Uninstall()
        {
            lock (OperationLock)
            {
                WithStagedTools(
                    (run, _) => Uninstall(run, FilterListed, DeviceNodeState, ProbeControlDevice, Log),
                    needsDriverPackage: false);
            }
        }

        /// <summary>
        /// The check upstream's watchdog service makes on x64
        /// (HidHide <c>Watchdog/App.cpp</c>, "Prevents bricked HID devices"):
        /// when a class still lists HidHide and the driver behind it is not
        /// there to load, take the entry out. Only an ARM64 machine needs it
        /// from here, because only there is HidHide installed without that
        /// watchdog. It says what it did in the diagnostics log, and it never
        /// throws.
        /// </summary>
        public static void RemoveDanglingFilters()
        {
            try
            {
                lock (OperationLock)
                {
                    ServiceState state = QueryServiceState(ServiceName);
                    var dangling = DanglingFilters(FilterListed, state);
                    if (dangling.Count == 0) return;

                    Log("HIDHIDE startup check: the service is " + state
                        + ", removing the class filter entries that name it on " + string.Join(", ", dangling));
                    WithStagedTools((run, _) => RemoveFilters(run, dangling, Log), needsDriverPackage: false);
                }
            }
            catch (Exception ex)
            {
                Log("HIDHIDE startup check failed: " + ex.Message);
            }
        }

        /// <summary>Takes HidHide off each class and reports every removal
        /// that did not succeed.</summary>
        internal static void RemoveFilters(NefconRunner run, IEnumerable<Guid> classes, Action<string> log)
        {
            foreach (Guid deviceClass in classes)
            {
                int? code = run(RemoveFilterArgs(deviceClass));
                if (code == null || !IsSuccess(code.Value, installing: false))
                    log("HIDHIDE startup check: the class filter on " + deviceClass.ToString("D")
                        + " was not removed (" + Describe(code) + ")");
            }
        }

        private static void Log(string line)
        {
            try { PadForge.Engine.SdlDiagLog.WriteLine(line); } catch { }
        }

        private const int ERROR_ACCESS_DENIED = 5;

        /// <summary>
        /// Whether one open of <c>\\.\HidHide</c> shows the driver there. An
        /// open that succeeds does. So does "access denied": the control
        /// device is exclusive (HidHide <c>ControlDevice.c</c>,
        /// WdfDeviceInitSetExclusive), and HidHide reports access denied when
        /// another client holds it (HidHideCLI <c>FilterDriverProxy.h</c>,
        /// "Returns ACCESS_DENIED when in use"). A second open measured on
        /// the x64 bench answered 5.
        /// </summary>
        internal static bool ControlDeviceAnswers(bool opened, int win32Error)
            => opened || win32Error == ERROR_ACCESS_DENIED;

        private static bool ProbeControlDevice()
        {
            bool opened = HidHideController.TryProbe(out int error);
            return ControlDeviceAnswers(opened, error);
        }

        /// <summary>The driver starts when its device node does, which can
        /// trail the install by a moment, so the probe is repeated for five
        /// seconds.</summary>
        private static bool WaitForControlDevice()
        {
            for (int i = 0; i < 25; i++)
            {
                if (ProbeControlDevice()) return true;
                System.Threading.Thread.Sleep(200);
            }
            return false;
        }

        private static void WithStagedTools(Action<NefconRunner, string> work, bool needsDriverPackage)
        {
            string staging = DriverInstaller.StagingFolder("HidHide");
            bool toolMayStillRun = false;
            try
            {
                string nefcon = DriverInstaller.ExtractEmbeddedResource(NefconResource, staging);
                if (needsDriverPackage) DriverInstaller.ExtractEmbeddedResource(DriverPackageResource, staging);

                int? Run(string arguments)
                {
                    // The test host installs and removes no HidHide: the
                    // tool is never started there, which reads as a tool
                    // that did not start. The installs are tested through a
                    // runner of the test's own.
                    if (HidHideController.SeamsOnlyForTest)
                    {
                        HidHideController.OffLimitsRefused?.Invoke("nefcon " + arguments);
                        return null;
                    }
                    var psi = new ProcessStartInfo
                    {
                        FileName = nefcon,
                        Arguments = arguments,
                        WorkingDirectory = staging,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    using var proc = Process.Start(psi);
                    if (proc == null) return null;
                    // Three minutes, the wait the x64 installer gets and the
                    // one Status_InstallerTimedOut states to the user.
                    if (!proc.WaitForExit(180_000)) { toolMayStillRun = true; return null; }
                    return proc.ExitCode;
                }

                work(Run, staging);
            }
            finally
            {
                // A tool that outlived its wait is still using these files.
                if (!toolMayStillRun) DriverInstaller.CleanupTempDir(staging);
            }
        }
    }
}
