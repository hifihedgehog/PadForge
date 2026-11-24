using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32;
using PadForge.Common.Input;

namespace PadForge.Common
{
    /// <summary>
    /// An installer ran to the end and did not succeed, or never came back.
    /// The facts ride on the properties so the caller can word the failure
    /// in the user's language. Message is an English fallback for a log.
    /// </summary>
    public sealed class InstallerFailedException : Exception
    {
        /// <summary>The installer's exit code. Meaningless when
        /// <see cref="TimedOut"/> is set.</summary>
        public int ExitCode { get; }

        /// <summary>The installer was still running when the wait ended, so
        /// nothing is known about how it finished.</summary>
        public bool TimedOut { get; }

        /// <summary>Windows started the installer without handing back a
        /// process, so there was nothing to wait on. This used to read as a
        /// timeout and told the user the installer was still running after
        /// three minutes, when nothing had been waited on at all.</summary>
        public bool NotTracked { get; }

        /// <summary>The installer may still be running, either way, so its
        /// staging folder stays.</summary>
        public bool OutcomeUnknown => TimedOut || NotTracked;

        /// <summary>ERROR_INSTALL_USEREXIT: the user pressed Cancel on the
        /// installer's own progress window.</summary>
        public bool UserCanceled => !OutcomeUnknown && ExitCode == 1602;

        public InstallerFailedException(int exitCode)
            : base("The installer exited with code " + exitCode + ".")
        {
            ExitCode = exitCode;
        }

        public InstallerFailedException()
            : base("The installer did not finish in time.")
        {
            TimedOut = true;
        }

        private InstallerFailedException(string message, bool notTracked)
            : base(message)
        {
            NotTracked = notTracked;
        }

        /// <summary>The failure for an installer Windows started without a
        /// process to follow.</summary>
        public static InstallerFailedException Untracked()
            => new("Windows started the installer without a process to follow.", true);

        /// <summary>The failure for a run that left no exit code.</summary>
        internal static InstallerFailedException NoExitCode(bool notTracked)
            => notTracked ? Untracked() : new InstallerFailedException();
    }

    /// <summary>Why a MIDI runtime or SteamVR operation stopped on one of
    /// PadForge's own checks.</summary>
    public enum DriverCheckFailure
    {
        /// <summary>The Visual C++ Redistributable download does not carry
        /// Microsoft's signature.</summary>
        VcRedistUnsigned,

        /// <summary>The MIDI runtime download does not match its published
        /// SHA-256.</summary>
        MidiRuntimeHashMismatch,

        /// <summary>No Windows MIDI Services runtime is installed to
        /// remove.</summary>
        NoMidiRuntime,

        /// <summary>A program held the old Visual C++ runtime open through
        /// every repair run, so Windows queued the new copy.</summary>
        VcRuntimeHeld,

        /// <summary>The Visual C++ runtime is still older than the minimum
        /// after its installer ran. The detail is that minimum.</summary>
        VcRuntimeStillOld,

        /// <summary>The Visual C++ Redistributable's run outlived its wait,
        /// or Windows started it without a process to follow, so the MIDI
        /// runtime's own installer never ran and its row in Settings has no
        /// result to show for it. The user installs again once the
        /// redistributable is done. The inner exception is the
        /// <see cref="InstallerFailedException"/> behind it.</summary>
        VcRedistStillRunning,

        /// <summary>MIDI was not released within its bound before the MIDI
        /// runtime's uninstall, so the uninstaller was not started.</summary>
        MidiReleaseTimedOut,

        /// <summary>The same, where what the release waited behind is a
        /// probe hung in a classic MIDI driver, which the status line names
        /// in place of the MIDI service.</summary>
        MidiReleaseTimedOutOnDriver,

        /// <summary>PadForge has no SteamVR install of its own to
        /// remove.</summary>
        NoOwnedSteamVr,

        /// <summary>SteamVR's server is running.</summary>
        SteamVrRunning,

        /// <summary>The recorded SteamVR folder is a drive root, which the
        /// uninstall will not delete. The detail is the folder.</summary>
        SteamVrFolderIsDriveRoot,

        /// <summary>The SteamVR folder is still there after the delete. The
        /// detail is the folder.</summary>
        SteamVrNotRemoved,

        /// <summary>The SteamVR install location is not a full path.</summary>
        SteamVrLocationNotFull,

        /// <summary>The SteamVR install location is a drive root.</summary>
        SteamVrLocationIsDriveRoot,

        /// <summary>steamcmd ran for 60 minutes without finishing.</summary>
        SteamCmdTimedOut,

        /// <summary>steamcmd finished and SteamVR is not in the install
        /// location. The detail is the folder, and the output is the end of
        /// what steamcmd printed.</summary>
        SteamVrDidNotLand,

        /// <summary>The S.T.R.I.K.E. 7 SDK download, or the library in it,
        /// does not match its known SHA-256 (#519).</summary>
        Strike7SdkHashMismatch,

        /// <summary>The Visual C++ 2008 runtime is still missing after its
        /// installer ran (#519).</summary>
        Vc2008RuntimeMissing,

        /// <summary>Force Dimension's Falcon driver package is gone from the
        /// SDK folder, or its catalog is (#521). The detail is the INF's
        /// path.</summary>
        FalconDriverPackageMissing,

        /// <summary>No Falcon is plugged in to install the driver on
        /// (#521).</summary>
        FalconNotPluggedIn,

        /// <summary>A Falcon is on another driver, which PadForge leaves in
        /// place (#521). The detail is that driver's service.</summary>
        FalconOnAnotherDriver,

        /// <summary>Windows did not stock Force Dimension's package, which
        /// includes the user declining its prompt to trust Force Dimension
        /// (#521).</summary>
        FalconDriverDeclined,

        /// <summary>The Falcon is not on WinUSB after the install
        /// (#521).</summary>
        FalconDriverNotBound,
    }

    /// <summary>
    /// A MIDI runtime or SteamVR operation stopped on one of PadForge's own
    /// checks, before or after an installer ran. As with
    /// <see cref="InstallerFailedException"/> and
    /// <see cref="HidHideSetupException"/>, the facts ride on the properties
    /// so the caller can word the failure in the user's language, and
    /// Message is an English fallback for a log.
    /// </summary>
    public sealed class DriverCheckException : Exception
    {
        public DriverCheckFailure Failure { get; }

        /// <summary>The value the wording names: a version or a folder.
        /// Empty when the failure names none.</summary>
        public string Detail { get; }

        /// <summary>The end of a tool's own output, in the tool's language.
        /// Empty when the failure carries none.</summary>
        public string Output { get; }

        public DriverCheckException(DriverCheckFailure failure, string message, string detail = null, string output = null,
            Exception inner = null)
            : base(message, inner)
        {
            Failure = failure;
            Detail = detail ?? string.Empty;
            Output = output ?? string.Empty;
        }
    }

    /// <summary>
    /// Handles HIDMaestro and HidHide install / uninstall in v3, plus legacy
    /// uninstall paths for v2 ViGEmBus and vJoy residue. Uses embedded
    /// bootstrapper executables that contain MSI packages.
    /// </summary>
    public static class DriverInstaller
    {
        // ─────────────────────────────────────────────
        //  ViGEmBus
        // ─────────────────────────────────────────────

        /// <summary>
        /// Uninstall ViGEmBus driver by looking up the MSI ProductCode in the
        /// Windows Uninstall registry and invoking <c>msiexec /x {ProductCode}</c>.
        /// Retained in v3 only for the first-run cleanup wizard that removes
        /// legacy v2 driver installs from upgrading users' systems — v3
        /// uses HIDMaestro. Registry-based lookup means we don't need to
        /// embed the 6 MB ViGEmBus installer any more; the MSI is already
        /// on the user's machine if they have it installed.
        /// </summary>
        public static void UninstallViGEmBus()
        {
            string productCode = FindUninstallProductCode("ViGEm");
            if (string.IsNullOrEmpty(productCode)) return;
            RunMsiElevated($"/x {productCode} /qb /norestart", absentIsSuccess: true);
        }

        /// <summary>
        /// Scans the Windows Uninstall registry (64- and 32-bit views) for a
        /// product whose <c>DisplayName</c> contains <paramref name="displayNameSubstring"/>
        /// and returns the matching subkey name (the MSI ProductCode GUID in
        /// the form <c>{XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX}</c>), or null
        /// when no match is found. Used for driver uninstalls that don't
        /// bundle their own MSI.
        /// </summary>
        private static string FindUninstallProductCode(string displayNameSubstring)
        {
            foreach (var entry in UninstallRegistry.Machine.Entries)
            {
                var name = entry.DisplayName;
                if (string.IsNullOrEmpty(name)) continue;
                if (name.IndexOf(displayNameSubstring, StringComparison.OrdinalIgnoreCase) < 0) continue;

                // Only a Windows Installer product has a ProductCode
                // msiexec can act on. A bootstrapper registers its
                // own entry under a brace GUID too, and that GUID
                // names a bundle msiexec has never heard of, so the
                // shape of the key alone does not decide it.
                string code = MsiProductCodeOrNull(entry.SubKeyName, entry.WindowsInstaller);
                if (code != null) return code;
            }
            return null;
        }

        // ─────────────────────────────────────────────
        //  HidHide
        // ─────────────────────────────────────────────

        private const string HidHideResourceName = "HidHide_1.5.230_x64.exe";

        /// <summary>
        /// A staging folder of one install's own, under the protected
        /// folder's Work folder (#597), for an installer, the package it
        /// unpacks or downloads, and a script PadForge writes and runs with
        /// rights. Before #597 these were in the user's temp folder, where
        /// any program of the user could swap a file between PadForge's check
        /// and the installer's read, or put a library beside an installer,
        /// which Windows loads from the installer's own folder first. The
        /// test host has no protected folder and keeps the temp folder, where
        /// it starts and installs nothing. Anywhere else a folder that cannot
        /// be made is an error, never the temp folder.
        /// </summary>
        internal static string StagingFolder(string purpose)
        {
            string folder = PadForge.Services.ProtectedWork.NewFolder(purpose);
            if (folder != null) return folder;
            if (OffLimitsForTest) return Path.Combine(Path.GetTempPath(), "PadForge_" + purpose, Guid.NewGuid().ToString("N"));
            throw new IOException("The protected folder for " + purpose + " could not be made.");
        }

        /// <summary>A tool of Windows' own by its full path in System32.
        /// Started by name alone, Windows would look for it in PadForge's own
        /// folder and the working folder first.</summary>
        internal static string System32Tool(string name)
            => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), name);

        /// <summary>
        /// A staging folder of this attempt's own.
        ///
        /// <para>There was one fixed folder, and every attempt began by
        /// clearing it. An installer that outlives the three-minute wait
        /// keeps reading its package from there, so a retry while it ran
        /// deleted the package from under it. Each attempt now gets its own
        /// folder and deletes that folder alone when it ends. A folder left
        /// by an attempt that timed out is never cleared from here, because
        /// nothing here can know that its installer has finished. A start of
        /// the app removes such folders once they are a day old
        /// (ProtectedWork.RemoveOld).</para>
        /// </summary>
        private static string NewHidHideStagingDir() => StagingFolder("HidHide");

        /// <summary>
        /// Installs HidHide, and announces PadForge to its driver while the
        /// install runs.
        ///
        /// <para>The driver starts inside the install and names each process
        /// by the first image it then sees mapped into it. Until it has
        /// PadForge's name it refuses PadForge at every device PadForge
        /// hides, and the hide is taken back
        /// (<see cref="HidHideController.AnnounceThisProcess"/>). Announced
        /// through the install, PadForge is known to the driver from the
        /// driver's first moments, and the first device hidden afterward
        /// stays hidden.</para>
        /// </summary>
        public static void InstallHidHide()
            => HidHideController.AnnounceThisProcessWhile(InstallHidHideNow);

        /// <summary>
        /// The install itself. On x64 it extracts the embedded bootstrapper,
        /// unpacks the MSI and runs msiexec /i with elevation.
        /// </summary>
        private static void InstallHidHideNow()
        {
            // A kernel driver cannot run emulated, so the MACHINE picks the
            // package, whichever build this is. ARM64 Windows takes upstream's
            // ARM64 driver package through HidHideArm64Installer. The MSI
            // below is x64 only.
            if (PadForge.Engine.PlatformSupport.IsArm64Machine)
            {
                HidHideArm64Installer.Install();
                return;
            }
            if (!PadForge.Engine.PlatformSupport.HidHideAvailable)
                throw new PlatformNotSupportedException(
                    "HidHide ships for x64 and ARM64 Windows only.");
            bool installerMayStillRun = false;
            string staging = NewHidHideStagingDir();
            try
            {
                var exePath = ExtractEmbeddedResource(HidHideResourceName, staging);
                var extractDir = ExtractInstallerBundle(exePath, staging);
                var msiPath = FindMsi(extractDir, "HidHide.msi", "HidHide*.msi");

                RunMsiElevated($"/i \"{msiPath}\" /qb /norestart", absentIsSuccess: false);
            }
            catch (InstallerFailedException ex) when (ex.OutcomeUnknown)
            {
                installerMayStillRun = true;
                throw;
            }
            finally
            {
                // An installer that outlived the wait is still reading the
                // package it was handed, so the package stays where it is.
                if (!installerMayStillRun) CleanupTempDir(staging);
            }
        }

        /// <summary>
        /// Uninstall the HidHide that is INSTALLED, by its registered
        /// ProductCode, which is the command Windows itself records for the
        /// product (<c>MsiExec.exe /X{ProductCode}</c>).
        ///
        /// <para>This used to hand msiexec the bundled package instead. A
        /// package path names the product inside that package, so the
        /// bundled 1.5.230 package removes an installed HidHide only when the
        /// two share a ProductCode. Windows Installer requires a new
        /// ProductCode for every major upgrade, msiexec answers 1605 for a
        /// product that is not installed, and the exit code was not read, so
        /// against an install with another ProductCode the button reported
        /// success and removed nothing.</para>
        ///
        /// <para>The bundled package is kept as the fallback for a
        /// registration that carries no usable ProductCode. There, 1605 stays
        /// a failure, because it only says the BUNDLED product is absent
        /// while something named HidHide is still registered.</para>
        /// </summary>
        public static void UninstallHidHide()
        {
            if (TryGetHidHideMsiInfo(out _, out string installedCode) && installedCode != null)
            {
                RunMsiElevated($"/x {installedCode} /qb /norestart", absentIsSuccess: true);
                return;
            }

            // An ARM64 install was never registered with Windows Installer.
            // It comes out the way it went in.
            if (PadForge.Engine.PlatformSupport.IsArm64Machine)
            {
                HidHideArm64Installer.Uninstall();
                return;
            }

            bool installerMayStillRun = false;
            string staging = NewHidHideStagingDir();
            try
            {
                var exePath = ExtractEmbeddedResource(HidHideResourceName, staging);
                var extractDir = ExtractInstallerBundle(exePath, staging);
                var msiPath = FindMsi(extractDir, "HidHide.msi", "HidHide*.msi");

                RunMsiElevated($"/x \"{msiPath}\" /qb /norestart", absentIsSuccess: false);
            }
            catch (InstallerFailedException ex) when (ex.OutcomeUnknown)
            {
                installerMayStillRun = true;
                throw;
            }
            finally
            {
                if (!installerMayStillRun) CleanupTempDir(staging);
            }
        }

        // ─────────────────────────────────────────────
        //  vJoy
        // ─────────────────────────────────────────────

        /// <summary>
        /// Uninstall vJoy driver. Removes device nodes first (so the driver
        /// can unload cleanly), then removes the driver from the store,
        /// deletes the service, and cleans up the install directory.
        /// Retained in v3 only for the first-run cleanup wizard that removes
        /// the legacy v2 vJoy install from upgrading users' systems —
        /// PadForge owns this custom SetupAPI deployment so the standard
        /// vJoy installer cannot remove it. Will be deleted after the
        /// migration window closes.
        /// </summary>
        public static void UninstallVJoy()
        {
            string vjoyDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "vJoy");
            var oemInfs = FindExtendedOemInfs();

            string scriptFolder = StagingFolder("vJoy");
            string scriptPath = Path.Combine(scriptFolder, "PadForge_vjoy_uninstall.cmd");
            // Each tool by its full path: cmd.exe looks in its working folder
            // first for a name alone.
            string pnputil = System32Tool("pnputil.exe"), sc = System32Tool("sc.exe"), reg = System32Tool("reg.exe"),
                timeout = System32Tool("timeout.exe"), powershell = Path.Combine(System32Tool("WindowsPowerShell"), "v1.0", "powershell.exe");
            using (var sw = new StreamWriter(scriptPath))
            {
                sw.WriteLine("@echo off");

                // IMPORTANT: Remove device nodes FIRST so the driver can fully
                // unload from the kernel. If we stop/delete the service while
                // devices are still attached, it gets stuck in STOP_PENDING.
                for (int i = 0; i <= 15; i++)
                    sw.WriteLine($"\"{pnputil}\" /remove-device \"ROOT\\HIDCLASS\\{i:D4}\" /subtree >nul 2>&1");
                sw.WriteLine($"\"{timeout}\" /t 2 /nobreak >nul 2>&1");

                // Now stop and delete the service (driver should be unloaded).
                sw.WriteLine($"\"{sc}\" stop vjoy >nul 2>&1");
                sw.WriteLine($"\"{timeout}\" /t 2 /nobreak >nul 2>&1");
                sw.WriteLine($"\"{sc}\" delete vjoy >nul 2>&1");
                // Fallback: if sc delete failed (e.g. STOP_PENDING), remove the
                // service registry keys from ALL ControlSets so it doesn't resurrect on reboot.
                foreach (string set in new[] { "CurrentControlSet", "ControlSet001", "ControlSet002", "ControlSet003" })
                    sw.WriteLine($"\"{reg}\" delete \"HKLM\\SYSTEM\\{set}\\Services\\vjoy\" /f >nul 2>&1");

                // Remove driver from driver store.
                foreach (var inf in oemInfs)
                    sw.WriteLine($"\"{pnputil}\" /delete-driver {inf} /uninstall /force >nul 2>&1");

                // Delete the install directory and stale driver binary.
                sw.WriteLine($"rmdir /s /q \"{vjoyDir}\" >nul 2>&1");
                sw.WriteLine($"del /f \"{Path.Combine(System32Tool("drivers"), "vjoy.sys")}\" >nul 2>&1");

                // Remove legacy Inno Setup uninstall registry entries (if present).
                sw.WriteLine("\"" + powershell + "\" -NoProfile -Command \"Get-ChildItem 'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall','HKLM:\\SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall' -EA SilentlyContinue | Where-Object { (Get-ItemProperty $_.PSPath -EA SilentlyContinue).DisplayName -like '*vJoy*' } | Remove-Item -Recurse -Force -EA SilentlyContinue\" >nul 2>&1");
            }

            RunElevated(System32Tool("cmd.exe"), $"/d /c \"\"{scriptPath}\"\"", out _);
            try { DeleteFolder(scriptFolder); } catch { }

            CleanExtendedRegistryArtifacts();
        }

        /// <summary>
        /// Light cleanup: removes only registry keys that can cause the vJoy installer
        /// to hang on reinstall. Does NOT touch pnputil or files — those are needed
        /// by the installer to register the driver via UpdateDriverForPlugAndPlayDevices.
        /// </summary>
        private static void CleanExtendedRegistryArtifacts()
        {
            try
            {
                string[] registryPaths =
                {
                    @"SYSTEM\CurrentControlSet\Services\vjoy",
                    @"SYSTEM\ControlSet001\Services\vjoy",
                    @"SYSTEM\ControlSet002\Services\vjoy",
                    @"SYSTEM\ControlSet003\Services\vjoy",
                    @"SYSTEM\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\VID_1234&PID_BEAD",
                    @"SYSTEM\ControlSet001\Services\EventLog\System\vjoy",
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Setup\PnpLockdownFiles\%SystemRoot%/System32/drivers/hidkmdf.sys",
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Setup\PnpLockdownFiles\%SystemRoot%/System32/drivers/vjoy.sys",
                    @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Setup\PnpLockdownFiles\%SystemRoot%/System32/drivers/hidkmdf.sys",
                    @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Setup\PnpLockdownFiles\%SystemRoot%/System32/drivers/vjoy.sys"
                };

                foreach (var path in registryPaths)
                {
                    try { DeleteRegistryTree(Registry.LocalMachine, path); }
                    catch { }
                }

                CleanExtendedDeviceClassEntries();

                try
                {
                    DeleteRegistryTree(Registry.CurrentUser,
                        @"System\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\VID_1234&PID_BEAD");
                }
                catch { }
            }
            catch { }
        }

        /// <summary>
        /// Removes vJoy device entries from the shared device class key
        /// {781ef630-72b2-11d2-b852-00c04fad5101} under ControlSet001\Control\Class.
        /// Only deletes subkeys where the "Class" value equals "vjoy".
        /// </summary>
        private static void CleanExtendedDeviceClassEntries()
        {
            const string classPath = @"SYSTEM\ControlSet001\Control\Class\{781ef630-72b2-11d2-b852-00c04fad5101}";
            try
            {
                using var classKey = Registry.LocalMachine.OpenSubKey(classPath, writable: false);
                if (classKey == null) return;

                foreach (var subName in classKey.GetSubKeyNames())
                {
                    try
                    {
                        using var sub = classKey.OpenSubKey(subName, writable: false);
                        var classValue = sub?.GetValue("Class") as string;
                        if (string.Equals(classValue, "vjoy", StringComparison.OrdinalIgnoreCase))
                        {
                            DeleteRegistryTree(Registry.LocalMachine, classPath + @"\" + subName);
                        }
                    }
                    catch { /* best effort */ }
                }
            }
            catch { /* best effort */ }
        }

        /// <summary>
        /// Runs pnputil /enum-drivers and finds OEM .inf names that belong to vJoy
        /// (published by "Shaul" or containing "vjoy" in the driver package name).
        /// </summary>
        private static string[] FindExtendedOemInfs()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = System32Tool("pnputil.exe"),
                    Arguments = "/enum-drivers",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };

                using var proc = StartProcess(psi);
                if (proc == null) return Array.Empty<string>();

                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(30_000);

                // Parse pnputil output. Format varies by Windows version, but each
                // driver entry has "Published Name" and either "Driver Package Provider"
                // or "Original Name" lines. We look for "shaul" or "vjoy" in the block
                // and extract the oem*.inf name.
                var results = new System.Collections.Generic.List<string>();
                string[] lines = output.Split('\n');
                string currentOem = null;
                bool isExtendedBlock = false;

                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();

                    // Detect the block start by its VALUE, not its label. The
                    // label is "Published Name" only on an English Windows;
                    // pnputil localizes it, so keying on that word made this
                    // whole parse return nothing on a German or Japanese
                    // machine and legacy drivers went undetected rather than
                    // reported. The value, "oemNN.inf", is locale-independent.
                    var oemHit = System.Text.RegularExpressions.Regex.Match(
                        // The word boundaries here were literal 0x08
                        // BACKSPACE bytes: round 35's scripted edit wrote
                        // the escape through a non-raw string, so the
                        // pattern required backspace characters pnputil
                        // never emits and matched NOTHING. The
                        // legacy-driver detection that round shipped was
                        // dead on arrival. Real word boundaries this time.
                        line, OemInfPattern,
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (oemHit.Success)
                    {
                        // Save any previous match.
                        if (isExtendedBlock && currentOem != null)
                            results.Add(currentOem);

                        currentOem = oemHit.Value;
                        isExtendedBlock = false;
                    }
                    // Check if this block mentions Shaul or vjoy.
                    else if (currentOem != null &&
                             (line.IndexOf("shaul", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              line.IndexOf("vjoy", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        isExtendedBlock = true;
                    }
                }

                // Don't forget the last block.
                if (isExtendedBlock && currentOem != null)
                    results.Add(currentOem);

                return results.ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        // ─────────────────────────────────────────────
        //  vJoy detection
        // ─────────────────────────────────────────────

        /// <summary>
        /// Checks whether the vJoy driver is installed.
        /// Detects both our minimal install (vjoy.sys in Program Files) and
        /// legacy Inno Setup installs (registry uninstall entry).
        /// </summary>
        public static bool IsExtendedInstalled()
        {
            // Primary: check for our minimal driver install.
            string vjoyDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "vJoy");
            if (File.Exists(Path.Combine(vjoyDir, "vjoy.sys")))
                return true;

            // Fallback: check for legacy Inno Setup install via registry.
            return !string.IsNullOrEmpty(GetVJoyVersionFromRegistry());
        }

        private static string GetVJoyVersionFromRegistry()
        {
            foreach (var entry in UninstallRegistry.Machine.Entries)
            {
                var name = entry.DisplayName;
                if (string.IsNullOrEmpty(name)) continue;

                if (name.IndexOf("vJoy", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                return entry.DisplayVersion ?? "Installed";
            }

            return null;
        }

        // ─────────────────────────────────────────────
        //  Windows MIDI Services runtime
        // ─────────────────────────────────────────────

        // Microsoft deleted the Windows MIDI Services runtime installers from
        // every microsoft/MIDI release on 2026-10-01, and the API moves into
        // Windows 11 25H2 with the late-November 2026 update
        // (MidiApiSelection). Until a PC has the in-box API, PadForge installs
        // its own build of the same runtime, 1.0.17-rc.4.25 from microsoft/MIDI
        // rc-4 under the MIT License, published at
        // hifihedgehog/PadForge-MIDI-Runtime. None of it ships inside PadForge,
        // and a download runs only when it matches the SHA-256 below.
        // Microsoft's own install, where a PC still has one, is found and
        // uninstalled here too.

        internal const string MidiRuntimeReleaseUrl =
            "https://github.com/hifihedgehog/PadForge-MIDI-Runtime/releases/download/v1.0.17-rc.4.25/";
        internal const string MidiRuntimeMsiX64 = "PadForge-MIDI-Runtime-1.0.17-rc.4.25-x64.msi";
        internal const string MidiRuntimeMsiArm64 = "PadForge-MIDI-Runtime-1.0.17-rc.4.25-arm64.msi";
        internal const string MidiRuntimeSha256X64 = "8d3cdaf9337af355d15ac48716d333818185c40d9d43a54f75016de12a55aec7";
        internal const string MidiRuntimeSha256Arm64 = "5f9b6e4a613c06df29eaecd9cddbb5439f6933ae200f758b1d0714ef57b26637";

        /// <summary>PadForge's runtime package and Microsoft's
        /// (src/app-sdk/sdk-runtime-installer/sdk-package in microsoft/MIDI),
        /// by upgrade code.</summary>
        internal const string PadForgeMidiRuntimeUpgradeCode = "{5C4B95B9-C1D9-4C00-8454-71560DDA7106}";
        internal const string MicrosoftMidiRuntimeUpgradeCode = "{297714BB-DD77-4748-A4C1-553AD66DA5D0}";

        /// <summary>Microsoft's bundle, which installed its runtime package
        /// with the MIDI Settings app and tools.</summary>
        private const string MicrosoftMidiBundleName = "Windows MIDI Services Runtime and Tools";

        /// <summary>Where the runtime finds its folder
        /// (MIDI_ROOT_APP_SDK_REG_KEY in microsoft/MIDI
        /// src/api/inc/MidiDefs.h). Both packages write it.</summary>
        private const string MidiRuntimeRegKey = @"SOFTWARE\Microsoft\Windows MIDI Services\Desktop App SDK Runtime";

        /// <summary>The runtime DLL was linked by MSVC 14.51, and the Visual
        /// C++ runtime a PC has must be at least as new as the tools that
        /// built a binary (Microsoft's "Latest supported Visual C++
        /// Redistributable downloads"). The x64 package carries the ARM64
        /// files as well, and Microsoft's own MIDI bundle installed that one
        /// on both architectures.</summary>
        internal static readonly Version MidiRuntimeVcMinimum = new(14, 51);
        private const string VcRedistUrl = "https://aka.ms/vc14/vc_redist.x64.exe";

        internal enum MidiRuntimeOwner { None, MicrosoftBundle, PadForgePackage, MicrosoftPackage }

        /// <summary>Which install provides the runtime, and what removes it:
        /// the bundle's UninstallString, or the package's ProductCode.
        /// Microsoft's bundle is checked first, since its uninstall removes
        /// its runtime package with everything else it installed.</summary>
        internal static (MidiRuntimeOwner Owner, string Target) FindMidiRuntime()
        {
            string bundle = FindMidiServicesUninstallString();
            if (bundle != null) return (MidiRuntimeOwner.MicrosoftBundle, bundle);
            string code = FindRelatedProduct(PadForgeMidiRuntimeUpgradeCode);
            if (code != null) return (MidiRuntimeOwner.PadForgePackage, code);
            code = FindRelatedProduct(MicrosoftMidiRuntimeUpgradeCode);
            if (code != null) return (MidiRuntimeOwner.MicrosoftPackage, code);
            return (MidiRuntimeOwner.None, null);
        }

        /// <summary>Whether uninstalling takes more than a runtime with it.
        /// Microsoft's bundle also removes its MIDI Settings app, console
        /// and PowerShell module, none of which Microsoft offers any more,
        /// so the row asks first. A runtime package holds the runtime
        /// alone, and the row can install a runtime again.</summary>
        internal static bool MidiUninstallIsIrreversible(MidiRuntimeOwner owner)
            => owner == MidiRuntimeOwner.MicrosoftBundle;

        /// <summary>
        /// Whether the App SDK runtime is installed, Microsoft's or
        /// PadForge's, read from Windows Installer and the uninstall entries,
        /// which the in-box API never creates. Does NOT load the runtime:
        /// that would lock its native DLLs in-process and block a clean
        /// uninstall.
        /// </summary>
        public static bool IsMidiRuntimeInstalled() => FindMidiRuntime().Owner != MidiRuntimeOwner.None;

        /// <summary>The installed runtime's version: the product version of
        /// the DLL the runtime's own registry value points at, such as
        /// 1.0.17-rc.4.25, else the bundle's DisplayVersion, else null. The
        /// card's second line used to print the product name again under a
        /// card already titled with it.</summary>
        public static string GetMidiRuntimeVersion()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(MidiRuntimeRegKey, false);
                if (key?.GetValue("Installed") is string folder)
                {
                    string dll = Path.Combine(folder, "Microsoft.Windows.Devices.Midi2.dll");
                    if (File.Exists(dll))
                    {
                        string version = FileVersionInfo.GetVersionInfo(dll).ProductVersion;
                        if (!string.IsNullOrEmpty(version)) return version;
                    }
                }
            }
            catch { }
            return GetMidiBundleVersion();
        }

        /// <summary>
        /// Downloads PadForge's build of the runtime for this machine's
        /// architecture, checks it against its published SHA-256 and installs
        /// it. When this PC's Visual C++ runtime is missing or older than
        /// <see cref="MidiRuntimeVcMinimum"/>, Microsoft's redistributable
        /// goes in first, and only once its Microsoft signature checks out.
        /// Runs on a worker thread. The app is always elevated, so neither
        /// installer prompts.
        /// </summary>
        public static void InstallMidiRuntime()
        {
            bool installerMayStillRun = false;
            string staging = StagingFolder("MidiRuntime");
            try
            {
                Directory.CreateDirectory(staging);
                using var http = new HttpClient();
                http.DefaultRequestHeaders.UserAgent.ParseAdd("PadForge");
                http.Timeout = TimeSpan.FromMinutes(10);

                if (!IsVcRuntimeAtLeast(MidiRuntimeVcMinimum))
                {
                    string redist = Path.Combine(staging, "vc_redist.x64.exe");
                    Download(http, VcRedistUrl, redist);
                    if (!IsSignedByMicrosoft(redist))
                        throw new DriverCheckException(DriverCheckFailure.VcRedistUnsigned,
                            "The Visual C++ Redistributable download is not signed by Microsoft.");
                    try
                    {
                        InstallVcRuntime(
                            arguments =>
                            {
                                int? exit = RunElevated(redist, arguments, out bool untracked);
                                return (exit, untracked);
                            },
                            () => IsVcRuntimeAtLeast(MidiRuntimeVcMinimum),
                            () => System.Threading.Thread.Sleep(VcRepairPauseMs),
                            VcRuntimeReplacementQueued);
                    }
                    catch (DriverCheckException ex) when (ex.Failure == DriverCheckFailure.VcRedistStillRunning)
                    {
                        // The redistributable may still be reading what it
                        // was handed.
                        installerMayStillRun = true;
                        throw;
                    }
                }

                bool arm64 = PadForge.Engine.PlatformSupport.IsArm64Machine;
                string name = arm64 ? MidiRuntimeMsiArm64 : MidiRuntimeMsiX64;
                string msi = Path.Combine(staging, name);
                Download(http, MidiRuntimeReleaseUrl + name, msi);
                if (!string.Equals(Sha256Hex(msi), arm64 ? MidiRuntimeSha256Arm64 : MidiRuntimeSha256X64,
                        StringComparison.OrdinalIgnoreCase))
                    throw new DriverCheckException(DriverCheckFailure.MidiRuntimeHashMismatch,
                        "The downloaded MIDI runtime does not match its published SHA-256.");
                try
                {
                    RunMsiElevated($"/i \"{msi}\" /qn /norestart MSIRESTARTMANAGERCONTROL=Disable", absentIsSuccess: false);
                }
                catch (InstallerFailedException ex) when (ex.OutcomeUnknown)
                {
                    installerMayStillRun = true;
                    throw;
                }
            }
            finally
            {
                // An installer that outlived its wait is still reading what
                // it was handed, so that stays where it is.
                if (!installerMayStillRun) CleanupTempDir(staging);
            }
        }

        /// <summary>
        /// Uninstalls the App SDK runtime through whichever install provides
        /// it (<see cref="FindMidiRuntime"/>): Microsoft's bundle through its
        /// cached bootstrapper, a package through msiexec. The in-box API is
        /// part of Windows and is never touched. The caller
        /// (MidiRuntimeSwitch.RunUninstall) first takes what the API in use
        /// calls for: nothing under the in-box API, a hold on Windows MIDI
        /// Services starting under the legacy API, which keeps running, and
        /// otherwise MIDI released and latched released. Waits up to five
        /// minutes and returns false when the uninstaller is still running
        /// then, and <paramref name="exited"/> completes when it exits, so
        /// the caller ends the hold it took then.
        /// </summary>
        public static bool UninstallMidiRuntime(out Task exited)
        {
            var (owner, target) = FindMidiRuntime();
            switch (owner)
            {
                case MidiRuntimeOwner.MicrosoftBundle:
                    return UninstallMidiBundle(target, out exited);
                case MidiRuntimeOwner.PadForgePackage:
                case MidiRuntimeOwner.MicrosoftPackage:
                    return UninstallMidiPackage(target, out exited);
                default:
                    throw new DriverCheckException(DriverCheckFailure.NoMidiRuntime,
                        "No Windows MIDI Services runtime is installed.");
            }
        }

        /// <summary>How long a runtime uninstaller is waited on before the
        /// row lets go of it. Tests shorten it.</summary>
        internal static int MidiUninstallWaitMs = 300_000;

        /// <summary>Waits <see cref="MidiUninstallWaitMs"/> for an
        /// uninstaller. True when it exited in time, and the caller still
        /// owns <paramref name="proc"/>. Otherwise <paramref name="exited"/>
        /// completes when it exits, reported through
        /// <paramref name="onExit"/>, and that task owns the Process from
        /// then on: disposing it here would drop the handle the exit is
        /// watched through.</summary>
        internal static bool WaitForUninstaller(Process proc, Action<int> onExit, out Task exited)
        {
            if (proc.WaitForExit(MidiUninstallWaitMs))
            {
                exited = Task.CompletedTask;
                return true;
            }
            exited = Task.Run(async () =>
            {
                try
                {
                    await proc.WaitForExitAsync().ConfigureAwait(false);
                    onExit?.Invoke(proc.ExitCode);
                }
                catch { /* the exit still ends the uninstall's hold */ }
                finally { proc.Dispose(); }
            });
            return false;
        }

        /// <summary>Runs the bundle's cached WiX Burn bootstrapper from its
        /// UninstallString with /quiet added.</summary>
        private static bool UninstallMidiBundle(string uninstallCmd, out Task exited)
        {
            // UninstallString is e.g.: "C:\...\Setup.exe"  /uninstall
            // Parse the quoted exe path and any existing arguments, then append /quiet.
            string exePath;
            string existingArgs = "";
            if (uninstallCmd.StartsWith('"'))
            {
                int closeQuote = uninstallCmd.IndexOf('"', 1);
                exePath = uninstallCmd.Substring(1, closeQuote - 1);
                existingArgs = uninstallCmd.Substring(closeQuote + 1).Trim();
            }
            else
            {
                int space = uninstallCmd.IndexOf(' ');
                exePath = space > 0 ? uninstallCmd.Substring(0, space) : uninstallCmd;
                if (space > 0) existingArgs = uninstallCmd.Substring(space + 1).Trim();
            }

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                // MSIRESTARTMANAGERCONTROL=Disable stops the packages asking
                // Restart Manager to close processes holding their files, so
                // files in use are scheduled for removal instead of PadForge
                // being shut down to reach them. Best-effort: a Burn bundle
                // only forwards properties its author declared, which is why
                // the window itself also declines RM's request outright (see
                // MainWindow.OnQueryEndSession).
                Arguments = $"{existingArgs} /quiet /norestart MSIRESTARTMANAGERCONTROL=Disable REBOOT=ReallySuppress".Trim(),
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var proc = StartProcess(psi);
            if (proc == null)
            {
                exited = Task.CompletedTask;
                return true;
            }
            if (WaitForUninstaller(proc,
                    code => PadForge.Engine.SdlDiagLog.WriteLine($"MIDI runtime bundle uninstaller exited late with {code}"),
                    out exited))
            {
                proc.Dispose();
                return true;
            }
            return false;
        }

        /// <summary>Removes a runtime package by ProductCode, with the same
        /// Restart Manager and reboot suppression as the bundle. A package
        /// that is already gone (1605) counts as removed. An msiexec that
        /// exits after the wait has its outcome logged when it
        /// exits.</summary>
        private static bool UninstallMidiPackage(string productCode, out Task exited)
        {
            var psi = new ProcessStartInfo
            {
                FileName = System32Tool("msiexec.exe"),
                Arguments = $"/x {productCode} /qn /norestart MSIRESTARTMANAGERCONTROL=Disable REBOOT=ReallySuppress",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var proc = StartProcess(psi);
            if (proc == null)
            {
                exited = Task.CompletedTask;
                return true;
            }
            if (!WaitForUninstaller(proc,
                    code => PadForge.Engine.SdlDiagLog.WriteLine(IsMsiSuccess(code, absentIsSuccess: true)
                        ? $"MIDI runtime package removed late ({code})"
                        : $"MIDI runtime package uninstaller failed late with {code}"),
                    out exited))
                return false;
            using (proc)
            {
                if (!IsMsiSuccess(proc.ExitCode, absentIsSuccess: true))
                    throw new InstallerFailedException(proc.ExitCode);
            }
            return true;
        }

        /// <summary>The pnputil "Published Name" value, oemNN.inf,
        /// locale-independent. A named constant so a test can pin it: the
        /// shipped version of this pattern carried literal backspace bytes
        /// where the word boundaries belong and matched nothing at all,
        /// invisibly, for two months.</summary>
        internal const string OemInfPattern = @"\boem\d+\.inf\b";

        /// <summary>
        /// Searches the registry Uninstall keys for Microsoft's Windows MIDI
        /// Services bundle and returns its UninstallString value.
        /// </summary>
        private static string FindMidiServicesUninstallString()
            => FindMidiBundle()?.UninstallString;

        /// <summary>The bundle's DisplayVersion, the way the HidHide and ViGEm
        /// rows read theirs.</summary>
        private static string GetMidiBundleVersion()
            => FindMidiBundle()?.DisplayVersion;

        /// <summary>The WiX Burn bootstrapper's own registration, not those
        /// of the MSI packages it installed, or null.</summary>
        private static UninstallRegistry.Entry? FindMidiBundle()
        {
            foreach (var entry in UninstallRegistry.Machine.Entries)
                if (entry.DisplayName is string name
                    && name.Equals(MicrosoftMidiBundleName, StringComparison.OrdinalIgnoreCase))
                    return entry;
            return null;
        }

        /// <summary>The ProductCode of an installed product with this
        /// upgrade code, or null.</summary>
        private static string FindRelatedProduct(string upgradeCode)
        {
            try
            {
                var product = new StringBuilder(39);
                return MsiEnumRelatedProducts(upgradeCode, 0, 0, product) == 0 ? product.ToString() : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Whether this PC's Visual C++ runtime, the System32 copy
        /// every process loads, is at least <paramref name="minimum"/>, with
        /// every DLL the runtime imports present.</summary>
        internal static bool IsVcRuntimeAtLeast(Version minimum)
        {
            try
            {
                string system = Environment.SystemDirectory;
                var needed = new System.Collections.Generic.List<string> { "msvcp140.dll", "msvcp140_atomic_wait.dll", "vcruntime140.dll" };
                // The x64 DLL also imports the x64-only exception-handling half.
                if (!PadForge.Engine.PlatformSupport.IsArm64Machine) needed.Add("vcruntime140_1.dll");
                foreach (string dll in needed)
                    if (!File.Exists(Path.Combine(system, dll))) return false;
                var info = FileVersionInfo.GetVersionInfo(Path.Combine(system, "msvcp140.dll"));
                return VcVersionAtLeast(info.FileMajorPart, info.FileMinorPart, minimum);
            }
            catch
            {
                return false;
            }
        }

        internal static bool VcVersionAtLeast(int major, int minor, Version minimum)
            => major > minimum.Major || (major == minimum.Major && minor >= minimum.Minor);

        /// <summary>Exit codes from Microsoft's redistributable that leave
        /// the runtime installed: done, a restart owed (3010) or started
        /// (1641), or a newer version already there (1638).</summary>
        internal static bool IsVcRedistSuccess(int exitCode) => exitCode is 0 or 3010 or 1641 or 1638;

        /// <summary>The redistributable's runs, in order. The first installs.
        /// The later ones repair: an install of the version already
        /// registered plans nothing for its packages, while a repair
        /// reinstalls every file of an equal or older version (WiX 3.14 Burn,
        /// engine/msiengine.cpp, the bundle engine Microsoft's redistributable
        /// runs on).</summary>
        internal static readonly string[] VcRedistRuns =
        {
            "/install /quiet /norestart",
            "/repair /quiet /norestart",
            "/repair /quiet /norestart",
        };

        /// <summary>The pause before each repair run, for the program holding
        /// the old runtime to let go of it.</summary>
        internal const int VcRepairPauseMs = 5_000;

        /// <summary>
        /// Runs Microsoft's redistributable until this PC's Visual C++ runtime
        /// is at least <see cref="MidiRuntimeVcMinimum"/>. Windows Installer
        /// renames a DLL that programs have loaded and puts the new copy in
        /// its place, so the update lands at once even when the
        /// redistributable answers 3010. Only a program that holds the old file
        /// open without letting it be renamed makes Windows queue the new copy
        /// for the next startup (the Windows Installer team's notes on its
        /// reboot behavior), and the repair runs try again once that program
        /// lets go. Burn refuses a run after an earlier one only when
        /// that one started a restart itself (engine/apply.cpp and
        /// engine/core.cpp), which /norestart rules out.
        /// </summary>
        /// <param name="run">Runs the redistributable with the given arguments
        /// and returns its exit code, or null with whether Windows handed back
        /// a process to follow.</param>
        /// <param name="runtimeCurrent">Whether the runtime is new enough
        /// now.</param>
        /// <param name="pause">Waits before a repair run.</param>
        /// <param name="replacementQueued">Whether Windows queued a runtime
        /// DLL for the next startup.</param>
        /// <exception cref="DriverCheckException">A run outlived its wait, or
        /// Windows handed back no process to follow
        /// (<see cref="DriverCheckFailure.VcRedistStillRunning"/>): the MIDI
        /// runtime's own installer never ran then. Or the Visual C++ runtime
        /// is still too old after every run
        /// (<see cref="DriverCheckFailure.VcRuntimeHeld"/>,
        /// <see cref="DriverCheckFailure.VcRuntimeStillOld"/>).</exception>
        internal static void InstallVcRuntime(Func<string, (int? Exit, bool Untracked)> run,
            Func<bool> runtimeCurrent, Action pause, Func<bool> replacementQueued)
        {
            for (int i = 0; i < VcRedistRuns.Length; i++)
            {
                if (i > 0) pause();
                var (exit, untracked) = run(VcRedistRuns[i]);
                if (exit == null)
                    throw new DriverCheckException(DriverCheckFailure.VcRedistStillRunning,
                        "The Visual C++ Redistributable installer is still running. Select Install again once it finishes.",
                        inner: InstallerFailedException.NoExitCode(untracked));
                if (!IsVcRedistSuccess(exit.Value)) throw new InstallerFailedException(exit.Value);
                if (runtimeCurrent()) return;
            }
            throw replacementQueued()
                ? new DriverCheckException(DriverCheckFailure.VcRuntimeHeld,
                    "A program holds the old Visual C++ runtime open, so Windows could not replace it. Close other programs and select Install again.")
                : new DriverCheckException(DriverCheckFailure.VcRuntimeStillOld,
                    $"The Visual C++ runtime is still older than {MidiRuntimeVcMinimum} after its installer ran.",
                    MidiRuntimeVcMinimum.ToString());
        }

        private const string SessionManagerKey = @"SYSTEM\CurrentControlSet\Control\Session Manager";

        /// <summary>Whether Windows holds a copy of one of the runtime's DLLs
        /// for the next startup, read from PendingFileRenameOperations.</summary>
        private static bool VcRuntimeReplacementQueued()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(SessionManagerKey, false);
                return VcRuntimeReplacementQueued(key?.GetValue("PendingFileRenameOperations") as string[],
                    Environment.SystemDirectory);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Whether a PendingFileRenameOperations list queues a new
        /// copy of a Visual C++ runtime DLL in <paramref name="systemFolder"/>.
        /// The list holds pairs: the queued file, then the file it replaces.
        /// A target carries the \??\ prefix, and a leading ! when it replaces
        /// an existing file. An empty target is a delete.</summary>
        internal static bool VcRuntimeReplacementQueued(string[] pendingRenames, string systemFolder)
        {
            if (pendingRenames == null || string.IsNullOrEmpty(systemFolder)) return false;
            string folder = systemFolder.TrimEnd('\\');
            for (int i = 1; i < pendingRenames.Length; i += 2)
            {
                string target = (pendingRenames[i] ?? string.Empty).TrimStart('!');
                if (target.StartsWith(@"\??\", StringComparison.Ordinal)) target = target.Substring(4);
                if (target.Length == 0) continue;
                if (!string.Equals(Path.GetDirectoryName(target), folder, StringComparison.OrdinalIgnoreCase)) continue;
                string name = Path.GetFileName(target);
                if (name.StartsWith("msvcp140", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("vcruntime140", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>Streams <paramref name="url"/> to <paramref name="path"/>.
        /// HttpClient follows the GitHub and aka.ms redirects.</summary>
        internal static void Download(HttpClient http, string url, string path)
        {
            ThrowIfDownloadOffLimits(url);
            using var response = http.Send(new HttpRequestMessage(HttpMethod.Get, url), HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            using var stream = response.Content.ReadAsStream();
            using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
            stream.CopyTo(file);
        }

        internal static string Sha256Hex(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
        }

        /// <summary>Whether the file's Authenticode signature is valid, by
        /// the check Windows runs before it trusts a download
        /// (WinVerifyTrust, WINTRUST_ACTION_GENERIC_VERIFY_V2), and was made
        /// by Microsoft Corporation.</summary>
        internal static bool IsSignedByMicrosoft(string path)
        {
            if (VerifyAuthenticode(path) != 0) return false;
            try
            {
                // CreateFromSignedFile is still the only managed API that
                // reads the signer out of a signed file (Ds3DriverInstaller
                // uses it the same way).
#pragma warning disable SYSLIB0057
                using var signed = X509Certificate2.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
                using var cert = new X509Certificate2(signed);
                return cert.Subject.Contains("O=Microsoft Corporation", StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        internal static int VerifyAuthenticode(string path)
        {
            var action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
            IntPtr fileInfo = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
            IntPtr data = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_DATA>());
            try
            {
                Marshal.StructureToPtr(new WINTRUST_FILE_INFO
                {
                    cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                    pcwszFilePath = path,
                }, fileInfo, false);
                var trust = new WINTRUST_DATA
                {
                    cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                    dwUIChoice = WTD_UI_NONE,
                    fdwRevocationChecks = WTD_REVOKE_NONE,
                    dwUnionChoice = WTD_CHOICE_FILE,
                    pFile = fileInfo,
                    dwStateAction = WTD_STATEACTION_VERIFY,
                };
                Marshal.StructureToPtr(trust, data, false);
                int result = WinVerifyTrust(IntPtr.Zero, ref action, data);

                // The verify leaves state behind, and a close action frees it.
                trust = Marshal.PtrToStructure<WINTRUST_DATA>(data);
                trust.dwStateAction = WTD_STATEACTION_CLOSE;
                Marshal.StructureToPtr(trust, data, false);
                WinVerifyTrust(IntPtr.Zero, ref action, data);
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(data);
                Marshal.DestroyStructure<WINTRUST_FILE_INFO>(fileInfo);
                Marshal.FreeHGlobal(fileInfo);
            }
        }

        private const uint WTD_UI_NONE = 2;
        private const uint WTD_REVOKE_NONE = 0;
        private const uint WTD_CHOICE_FILE = 1;
        private const uint WTD_STATEACTION_VERIFY = 1;
        private const uint WTD_STATEACTION_CLOSE = 2;
        private static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WINTRUST_FILE_INFO
        {
            public uint cbStruct;
            [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINTRUST_DATA
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll", ExactSpelling = true)]
        private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, IntPtr pWVTData);

        [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "MsiEnumRelatedProductsW")]
        private static extern uint MsiEnumRelatedProducts(string lpUpgradeCode, uint dwReserved, uint iProductIndex, StringBuilder lpProductBuf);

        // ─────────────────────────────────────────────
        //  SteamVR (Steam-free install, issue #49)
        // ─────────────────────────────────────────────

        /// <summary>Default landing spot for the Steam-free SteamVR install
        /// when the user picks nothing else. HIDMaestro's
        /// discovery checks this conventional folder last, and the explicit
        /// path hint below makes it authoritative regardless.</summary>
        public const string SteamVrInstallDir = @"C:\SteamVR";

        /// <summary>Valve's own steamcmd bootstrap. Needs no Steam client
        /// and no account: SteamVR (app 250820) is anonymous-licensed, the
        /// path HIDMaestro verified during v1.6.0 development.</summary>
        private const string SteamCmdZipUrl =
            "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip";

        /// <summary>steamcmd's folder, kept between runs since steamcmd updates
        /// itself there. It runs with PadForge's rights, so it lives under the
        /// protected folder (#597): from the temp folder, a library put beside
        /// it by any program of the user would run with those rights. The test
        /// host keeps the temp folder, where it starts nothing.</summary>
        private static string GetSteamVrTempDir()
            => PadForge.Services.ProtectedWork.Folder("SteamCmd")
               ?? (OffLimitsForTest ? Path.Combine(Path.GetTempPath(), "PadForge_SteamCmd")
                   : throw new IOException("The protected folder for steamcmd could not be made."));

        /// <summary>Test seam for <see cref="InstallSteamVRAsync"/>: throw
        /// after the argument guards, before any network or process work.
        /// Set only by SteamVrInstallGuardTests.</summary>
        internal static bool SteamVrInstallStopAfterGuards;

        /// <summary>The HKLM key HIDMaestro's <c>FindSteamVR</c> consults
        /// first. Mirrors VrDriverBuilder's constants; the value written by
        /// <c>HMVR.SetSteamVRPathHint</c> is both the discovery mechanism and
        /// PadForge's ownership marker for the Steam-free shape.</summary>
        private const string HmRegKeyPath = @"SOFTWARE\HIDMaestro";
        private const string SteamVrPathRegValue = "SteamVRPath";

        /// <summary>Normalizes a user-chosen install dir: trims, drops
        /// trailing separators (but keeps a drive root intact), and falls
        /// back to <see cref="SteamVrInstallDir"/> for null/blank.</summary>
        private static string NormalizeSteamVrDir(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return SteamVrInstallDir;
            dir = dir.Trim();
            string trimmed = dir.TrimEnd('\\', '/');
            // "C:\" trims to "C:", which means CWD-relative. Keep the root.
            return trimmed.Length >= dir.Length - 1 && trimmed.EndsWith(":", StringComparison.Ordinal)
                ? trimmed + Path.DirectorySeparatorChar
                : trimmed;
        }

        private static bool SameDir(string a, string b) =>
            !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b)
            && string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'),
                             StringComparison.OrdinalIgnoreCase);

        /// <summary>True for a bare drive root ("C:\", "D:"). Both the
        /// installer and the uninstaller refuse these: a payload at a drive
        /// root is never a legitimate target, and the uninstall side would
        /// otherwise be one hand-edited registry hint away from
        /// Directory.Delete on an entire drive.</summary>
        private static bool IsDriveRoot(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return false;
            string t = dir.Trim().TrimEnd('\\', '/');
            return t.Length == 2 && t[1] == ':';
        }

        /// <summary>
        /// The directory of the Steam-free SteamVR install PADFORGE owns, or
        /// null. Owned means: the HIDMaestro path hint exists, the payload it
        /// points at is real (vrpathreg.exe present), and neither Steam's own
        /// "Steam App 250820" uninstall entry nor the default Steam library
        /// resolves to the same directory. A Steam-client install therefore
        /// never reads as owned, which is what gates the Uninstall button.
        /// </summary>
        public static string GetOwnedSteamVrDir()
        {
            try
            {
                string hinted;
                using (var hm = Registry.LocalMachine.OpenSubKey(HmRegKeyPath))
                    hinted = hm?.GetValue(SteamVrPathRegValue) as string;
                if (string.IsNullOrWhiteSpace(hinted)) return null;
                if (!File.Exists(Path.Combine(hinted, "bin", "win64", "vrpathreg.exe")))
                    return null;

                // Steam's 32-bit client writes its per-app uninstall entries
                // under the WOW6432Node view; a default 64-bit read misses
                // them entirely. Check both views so a custom-library
                // Steam install (which the library check below cannot see)
                // still disqualifies ownership.
                foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
                {
                    using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using var k = baseKey.OpenSubKey(
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 250820");
                    if (k?.GetValue("InstallLocation") is string steamApp && SameDir(steamApp, hinted))
                        return null;
                }

                using (var steam = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam"))
                    if (steam?.GetValue("InstallPath") is string steamRoot
                        && SameDir(Path.Combine(steamRoot, "steamapps", "common", "SteamVR"), hinted))
                        return null;

                return hinted;
            }
            catch { return null; }
        }

        /// <summary>
        /// The decision behind <see cref="ContainOwnedSteamVrDataPaths"/>,
        /// separated from the registry and the file so the refusals can be
        /// tested. Answers true only for an install PadForge owns whose data
        /// paths are not already inside it.
        ///
        /// <para>The refusals are the point. A SteamVR that PadForge did not
        /// install, and a registry pointing at somebody else's runtime, are
        /// both none of PadForge's business, and rewriting either would move
        /// a stranger's log directory out from under them.</para>
        /// </summary>
        internal static bool ShouldContainDataPaths(
            string ownedDir, string runtimeInRegistry,
            string configInRegistry, string logInRegistry,
            out string wantConfig, out string wantLog)
        {
            wantConfig = wantLog = null;
            // No ownership marker: PadForge never installed this one.
            if (string.IsNullOrWhiteSpace(ownedDir)) return false;
            // The registry names a DIFFERENT runtime, so its data paths
            // belong to that runtime rather than to ours.
            if (!SameDir(runtimeInRegistry, ownedDir)) return false;

            wantConfig = Path.Combine(ownedDir, "config");
            wantLog = Path.Combine(ownedDir, "logs");
            return !(SameDir(configInRegistry, wantConfig) && SameDir(logInRegistry, wantLog));
        }

        /// <summary>
        /// Points a PadForge-owned SteamVR's config and log directories INSIDE
        /// the install, in the path registry every OpenVR process reads.
        ///
        /// <para>Valve's tooling writes openvrpaths.vrpath with config and log
        /// as SIBLINGS of the runtime, named by appending "-config" and
        /// "-logs". At the default install that puts two folders at the root
        /// of the system drive, and the consumer's own VR_LOG_PATH override
        /// moves only PadForge's copy: vrserver, vrmonitor and vrcompositor
        /// read this same file and would recreate both the moment SteamVR
        /// actually ran. Fixing the file fixes it for every process.</para>
        ///
        /// <para>Only for an install PadForge owns, which means one whose path
        /// PadForge itself recorded (HIDMaestro writes that hint nowhere, it
        /// only reads it) and which resolves to neither Steam's app entry nor
        /// its library. A preexisting or Steam-client SteamVR is left exactly
        /// as it is. Rewrites nothing when the paths are already inside, so
        /// this is safe on every launch.</para>
        /// </summary>
        /// <returns>True when the file was rewritten.</returns>
        public static bool ContainOwnedSteamVrDataPaths()
        {
            try
            {
                string dir = GetOwnedSteamVrDir();
                if (string.IsNullOrWhiteSpace(dir)) return false;

                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string reg = Path.Combine(local, "openvr", "openvrpaths.vrpath");
                if (!File.Exists(reg)) return false;

                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(reg));
                var root = doc.RootElement;
                if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return false;

                static string First(System.Text.Json.JsonElement o, string name)
                    => o.TryGetProperty(name, out var a)
                       && a.ValueKind == System.Text.Json.JsonValueKind.Array
                       && a.GetArrayLength() == 1
                       ? a[0].GetString() : null;

                bool rewrite = ShouldContainDataPaths(dir, First(root, "runtime"),
                    First(root, "config"), First(root, "log"),
                    out string wantConfig, out string wantLog);
                // Reads end here. The test host makes no folder in a SteamVR
                // install and rewrites no path registry.
                if (OffLimitsForTest) return false;
                // Nothing to point at is a real state, not a theoretical one.
                // steamcmd finishes with "validate", which deletes anything in
                // the install that is not in Valve's manifest, so the two
                // directories created moments earlier by the install-time call
                // are gone by the time the download settles while the registry
                // entries, which live under LOCALAPPDATA, survive. Make them
                // whenever this runs, including on the launch after, and not
                // only on the launch that rewrites.
                if (wantConfig != null)
                {
                    try
                    {
                        Directory.CreateDirectory(wantConfig);
                        Directory.CreateDirectory(wantLog);
                    }
                    catch { }
                }
                if (!rewrite) return false;

                // Copy every other key through untouched: external_drivers
                // carries HIDMaestro's own driver registration, and dropping
                // it would unregister the VR slots.
                var buffer = new System.IO.MemoryStream();
                using (var w = new System.Text.Json.Utf8JsonWriter(buffer,
                    new System.Text.Json.JsonWriterOptions { Indented = true }))
                {
                    w.WriteStartObject();
                    foreach (var prop in root.EnumerateObject())
                    {
                        if (prop.NameEquals("config") || prop.NameEquals("log")) continue;
                        prop.WriteTo(w);
                    }
                    w.WriteStartArray("config"); w.WriteStringValue(wantConfig); w.WriteEndArray();
                    w.WriteStartArray("log"); w.WriteStringValue(wantLog); w.WriteEndArray();
                    w.WriteEndObject();
                }
                File.WriteAllBytes(reg, buffer.ToArray());
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Removes the Steam-free SteamVR install PadForge owns: deletes the
        /// payload directory, clears the HIDMaestro path hint, and resets the
        /// availability cache so the VR slot gates drop immediately. Refuses
        /// while vrserver runs (deleting a live runtime out from under
        /// itself), and refuses when no owned install exists. A Steam-client
        /// install is never touched, by design. The HM driver registration
        /// needs no separate cleanup: vrpathreg's record lives inside the
        /// SteamVR install's own config and dies with the directory.
        /// </summary>
        public static void UninstallSteamVR()
        {
            string dir = GetOwnedSteamVrDir()
                ?? throw new DriverCheckException(DriverCheckFailure.NoOwnedSteamVr,
                    "PadForge has no SteamVR install of its own to remove.");
            if (PadForge.Common.Input.ProcessPresence.RunningAmong(new[] { "vrserver" }).Count > 0)
                throw new DriverCheckException(DriverCheckFailure.SteamVrRunning, "SteamVR is running.");
            // Independent of the install-side refusal: the hint is a plain
            // registry value anyone can edit, and this is the line that
            // recursively deletes whatever it names.
            if (IsDriveRoot(dir))
                throw new DriverCheckException(DriverCheckFailure.SteamVrFolderIsDriveRoot,
                    "The recorded SteamVR folder " + dir + " is a drive root, so PadForge will not delete it.", dir);

            // PadForge is holding one of the files it is about to delete.
            // The consumer loads SteamVR's own openvr_api.dll into this
            // process and cached the handle forever, so the recursive delete
            // below skipped that single file, the uninstall still reported
            // success, and openvr_api.dll stayed on disk under a directory the
            // user believed was gone. Unload first, then delete.
            Input.OpenVrConsumerService.ReleaseRuntime();

            DeleteSteamVrFolder(dir,
                DeleteFolder,
                Directory.Exists,
                () => System.Threading.Thread.Sleep(300));

            ClearSteamVrPathHint();

            HMaestroVRController.ResetAvailability();
        }

        /// <summary>How many times the SteamVR folder's delete is
        /// tried.</summary>
        internal const int SteamVrDeleteTries = 10;

        /// <summary>
        /// Deletes the SteamVR folder and confirms it is gone. Windows drops
        /// a freed module's file lock asynchronously, so the first delete
        /// after an unload can still lose the race, and the delete is tried
        /// up to <see cref="SteamVrDeleteTries"/> times with a pause between
        /// rather than leaving a half-removed install behind. A folder still
        /// there after the last try is
        /// <see cref="DriverCheckFailure.SteamVrNotRemoved"/>, whether that
        /// try threw or left part of it. The last try's own exception used
        /// to leave the uninstall in Windows' words, where every other
        /// failure of it is a typed check the status line words in the UI
        /// language.
        /// </summary>
        internal static void DeleteSteamVrFolder(string dir, Action<string> delete, Func<string, bool> exists, Action pause)
        {
            for (int attempt = 1; attempt <= SteamVrDeleteTries; attempt++)
            {
                try
                {
                    delete(dir);
                    break;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                if (attempt < SteamVrDeleteTries) pause();
            }

            // "Uninstalled" has to mean the directory is gone. Reporting
            // success over a partial delete is what let a locked
            // openvr_api.dll survive unnoticed in the first place.
            if (exists(dir))
                throw new DriverCheckException(DriverCheckFailure.SteamVrNotRemoved,
                    "SteamVR was not fully removed: " + dir + " still exists. "
                    + "Close anything using SteamVR and try again.", dir);
        }

        /// <summary>
        /// Installs SteamVR without Steam: downloads Valve's steamcmd,
        /// runs the anonymous app_update for app 250820 into
        /// <paramref name="installDir"/> (default
        /// <see cref="SteamVrInstallDir"/>), then records the path hint so
        /// HIDMaestro's discovery (which reads no Steam registry keys for
        /// this shape) finds it from now on. The payload is several GB, so
        /// this can run for many minutes on slow links; steamcmd prints
        /// progress to its own console which stays hidden here.
        /// </summary>
        public static async Task InstallSteamVRAsync(string installDir = null)
        {
            string targetDir = NormalizeSteamVrDir(installDir);
            // A relative path would land the payload relative to steamcmd's
            // own working directory (the temp staging dir), then fail the
            // vrpathreg verdict with a confusing message. Refuse up front.
            if (!Path.IsPathRooted(targetDir))
                throw new DriverCheckException(DriverCheckFailure.SteamVrLocationNotFull,
                    "The SteamVR install location must be a full path, such as C:\\SteamVR.");
            // A drive root is never a legitimate target, and allowing one
            // would arm the uninstall side with Directory.Delete on the
            // whole drive. Refuse it here too, symmetrically.
            if (IsDriveRoot(targetDir))
                throw new DriverCheckException(DriverCheckFailure.SteamVrLocationIsDriveRoot,
                    "The SteamVR install location cannot be a drive root. Pick a folder, such as C:\\SteamVR.");
            // Test seam. Without it, the guard tests EXECUTE the real
            // installer whenever a guard regresses: audit round 43's
            // mutation run launched a live steamcmd toward C:\ while
            // proving exactly that hazard. With the seam, a regressed
            // guard still reddens the test (wrong exception type) and
            // touches neither the network nor a process.
            if (SteamVrInstallStopAfterGuards)
                throw new InvalidOperationException("Test seam: guards passed.");
            var tempDir = GetSteamVrTempDir();
            Directory.CreateDirectory(tempDir);
            try
            {
                var zipPath = Path.Combine(tempDir, "steamcmd.zip");
                using (var http = new HttpClient())
                {
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("PadForge");
                    http.Timeout = TimeSpan.FromMinutes(10);
                    ThrowIfDownloadOffLimits(SteamCmdZipUrl);
                    using var response = await http.GetAsync(SteamCmdZipUrl,
                        HttpCompletionOption.ResponseHeadersRead);
                    response.EnsureSuccessStatusCode();
                    using var stream = await response.Content.ReadAsStreamAsync();
                    using var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write);
                    await stream.CopyToAsync(fs);
                }
                System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, tempDir, overwriteFiles: true);

                // force_install_dir BEFORE login, per Valve's steamcmd
                // contract; +quit so the process exits when done. The dir is
                // quoted because it is user-chosen now and may carry spaces.
                // Two quirks this loop absorbs, both reproduced on this
                // machine (2026-08-08): the FIRST run self-updates, prints
                // "Update complete, launching..." and EXITS with code 7
                // without ever executing the + commands (the relaunch
                // detaches), so the command line must be run again; and exit
                // codes are not trustworthy in general. Presence of
                // vrpathreg.exe is the only install verdict.
                string vrpathreg = Path.Combine(targetDir, "bin", "win64", "vrpathreg.exe");
                string lastTail = "";
                for (int attempt = 1; attempt <= 3 && !File.Exists(vrpathreg); attempt++)
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = Path.Combine(tempDir, "steamcmd.exe"),
                        Arguments = $"+force_install_dir \"{targetDir}\" +login anonymous +app_update 250820 validate +quit",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        WorkingDirectory = tempDir
                    };
                    using var proc = StartProcess(psi);
                    if (proc == null) continue;
                    var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                    using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(60));
                    try
                    {
                        await proc.WaitForExitAsync(cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        try { proc.Kill(entireProcessTree: true); } catch { }
                        throw new DriverCheckException(DriverCheckFailure.SteamCmdTimedOut,
                            "steamcmd did not finish within 60 minutes.");
                    }
                    string outText = await stdoutTask;
                    lastTail = outText.Length > 400 ? outText[^400..] : outText;
                }

                if (!File.Exists(vrpathreg))
                    throw new DriverCheckException(DriverCheckFailure.SteamVrDidNotLand,
                        "steamcmd ran but SteamVR did not land in " + targetDir
                        + ". steamcmd said: ..." + lastTail, targetDir, lastTail);

                // Steam-free installs write no registry keys of their own;
                // the hint makes discovery unconditional. Requires admin,
                // which PadForge always has.
                SetSteamVrPathHint(targetDir);
                // Valve's tooling has just written the registry with the
                // root-level "-config"/"-logs" siblings. Move them inside.
                ContainOwnedSteamVrDataPaths();
                HMaestroVRController.ResetAvailability();
            }
            finally
            {
                CleanupTempDir(tempDir);
            }
        }

        // ─────────────────────────────────────────────
        //  ViGEmBus detection
        // ─────────────────────────────────────────────

        /// <summary>
        /// Returns the installed ViGEmBus version string, or null if not found in registry.
        /// </summary>
        public static string GetViGEmVersion()
        {
            foreach (var entry in UninstallRegistry.Machine.Entries)
            {
                var name = entry.DisplayName;
                if (string.IsNullOrEmpty(name)) continue;

                if (name.IndexOf("ViGEm", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                return entry.DisplayVersion;
            }

            return null;
        }

        // ─────────────────────────────────────────────
        //  HidHide detection
        // ─────────────────────────────────────────────

        /// <summary>
        /// Whether HidHide is installed, and its version, from one look: a
        /// Windows Installer registration on any machine, or on ARM64 the
        /// service, class filter and device node that upstream's manual
        /// install leaves, which registers nothing with Windows Installer.
        /// The version is the MSI's where there is one, else the driver
        /// file's own, and "Installed" when neither can be read.
        ///
        /// <para>This was two methods, one for each answer, and the status
        /// timer called both every five seconds on the UI thread. Each made
        /// the same look, so every tick walked the uninstall registry twice,
        /// and on ARM64 enumerated the machine's devices twice.</para>
        /// </summary>
        public static bool TryGetHidHideStatus(out string version)
        {
            if (TryGetHidHideMsiInfo(out string msiVersion, out _))
            {
                version = string.IsNullOrEmpty(msiVersion) ? "Installed" : msiVersion;
                return true;
            }
            if (PadForge.Engine.PlatformSupport.IsArm64Machine && HidHideArm64Installer.IsInstalled())
            {
                version = HidHideArm64Installer.InstalledDriverVersion() ?? "Installed";
                return true;
            }
            version = null;
            return false;
        }

        private static bool TryGetHidHideMsiInfo(out string displayVersion, out string productCode)
        {
            displayVersion = null;
            productCode = null;
            bool found = false;

            foreach (var entry in UninstallRegistry.Machine.Entries)
            {
                var name = entry.DisplayName;
                if (string.IsNullOrEmpty(name)) continue;

                if (name.IndexOf("HidHide", StringComparison.OrdinalIgnoreCase) < 0 &&
                    name.IndexOf("HID Hide", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                // "Installed" is any registration that names it, as
                // it always was. The ProductCode is narrower: only a
                // Windows Installer product has one msiexec can act
                // on, and a bootstrapper's own entry can sit in front
                // of it under a brace GUID that is not a ProductCode.
                // So the walk carries on past a match that has no
                // code, and stops at the first one that does.
                found = true;
                string code = MsiProductCodeOrNull(entry.SubKeyName, entry.WindowsInstaller);
                if (displayVersion == null || code != null)
                    displayVersion = entry.DisplayVersion ?? displayVersion;
                if (code != null)
                {
                    productCode = code;
                    return true;
                }
            }

            return found;
        }

        /// <summary>
        /// The subkey name as an MSI ProductCode, or null when the entry is
        /// not a Windows Installer product. Windows Installer marks its own
        /// entries with a WindowsInstaller DWORD of 1 and names them by
        /// ProductCode. Anything else under Uninstall is some other
        /// installer's bookkeeping, whatever its key looks like.
        /// </summary>
        internal static string MsiProductCodeOrNull(string subKeyName, object windowsInstallerValue)
        {
            if (!(windowsInstallerValue is int flag) || flag != 1) return null;
            return Guid.TryParseExact(subKeyName, "B", out _) ? subKeyName : null;
        }

        /// <summary>
        /// Whether msiexec's exit code means the work is done. 3010 and 1641
        /// are success with a restart owed or already started. 1605 means the
        /// product named is not installed, which finishes an uninstall that
        /// named an INSTALLED product and fails everything else.
        /// </summary>
        internal static bool IsMsiSuccess(int exitCode, bool absentIsSuccess)
            => exitCode == 0
            || exitCode == 3010
            || exitCode == 1641
            || (absentIsSuccess && exitCode == 1605);

        // ─────────────────────────────────────────────
        //  Shared helpers
        // ─────────────────────────────────────────────

        /// <summary>
        /// Extract an embedded resource to a temp directory.
        /// Returns the full path to the extracted file.
        /// </summary>
        internal static string ExtractEmbeddedResource(string resourceFileName, string tempDir)
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceNames = assembly.GetManifestResourceNames();

            var resourceName = resourceNames.FirstOrDefault(
                x => x.IndexOf(resourceFileName, StringComparison.OrdinalIgnoreCase) >= 0);

            if (string.IsNullOrEmpty(resourceName))
                throw new FileNotFoundException(
                    $"Embedded resource '{resourceFileName}' not found. " +
                    $"Available: {string.Join(", ", resourceNames)}");

            Directory.CreateDirectory(tempDir);
            var tempPath = Path.Combine(tempDir, resourceFileName);

            using (var stream = assembly.GetManifestResourceStream(resourceName))
            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
            {
                stream.CopyTo(fs);
            }

            return tempPath;
        }

        /// <summary>
        /// Run the bootstrapper's /extract to unpack the MSI and supporting files.
        /// Returns the path to the extraction folder.
        /// </summary>
        private static string ExtractInstallerBundle(string exePath, string tempDir)
        {
            var extractDir = Path.Combine(tempDir, "Extracted");

            if (Directory.Exists(extractDir))
                DeleteFolder(extractDir);

            Directory.CreateDirectory(extractDir);

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"/extract \"{extractDir}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (var proc = StartProcess(psi))
            {
                proc?.WaitForExit(60_000);
            }

            return extractDir;
        }

        /// <summary>
        /// Find an MSI file in the extracted bundle contents.
        /// </summary>
        private static string FindMsi(string extractDir, string primaryName, string fallbackPattern)
        {
            var files = Directory.GetFiles(extractDir, primaryName, SearchOption.AllDirectories);
            if (files.Length > 0)
                return files[0];

            files = Directory.GetFiles(extractDir, fallbackPattern, SearchOption.AllDirectories);
            if (files.Length > 0)
                return files[0];

            throw new FileNotFoundException(
                $"Could not find {primaryName} in extracted bundle at '{extractDir}'.");
        }

        /// <summary>
        /// Run msiexec.exe with elevation (UAC prompt) and hold it to its
        /// exit code. The wait's result and the code were both ignored once,
        /// so a failed install, a canceled one and one still running after
        /// three minutes all came back as success.
        /// </summary>
        /// <exception cref="InstallerFailedException">msiexec failed, was
        /// canceled from its own window, or outlived the wait.</exception>
        private static void RunMsiElevated(string arguments, bool absentIsSuccess)
        {
            int? exitCode = RunElevated(System32Tool("msiexec.exe"), arguments, out bool untracked);
            if (exitCode == null) throw InstallerFailedException.NoExitCode(untracked);
            if (!IsMsiSuccess(exitCode.Value, absentIsSuccess))
                throw new InstallerFailedException(exitCode.Value);
        }

        /// <summary>
        /// Run an executable with elevation (UAC prompt). Returns its exit
        /// code, or null when there is none to read: it was still running
        /// after three minutes, or Windows started it without handing back a
        /// process (<paramref name="notTracked"/>). What an exit code means
        /// belongs to the caller: msiexec's are documented, a cleanup
        /// script's are not.
        /// </summary>
        private static int? RunElevated(string fileName, string arguments, out bool notTracked)
        {
            notTracked = false;
            using var proc = StartProcess(ElevatedStart(fileName, arguments, Environment.IsPrivilegedProcess));
            // ShellExecute can start the work without handing a process back.
            // Nothing can be waited on then, and nothing is known.
            if (proc == null)
            {
                notTracked = true;
                return null;
            }
            return proc.WaitForExit(180_000) ? proc.ExitCode : (int?)null;
        }

        /// <summary>
        /// How <see cref="RunElevated"/> starts a tool (#597). A process with
        /// rights starts it itself, hidden, with the variables every start
        /// with rights gets (<see cref="PadForge.Services.RightsVariables"/>):
        /// the shell's runas verb would hand the tool a copy of PadForge's
        /// own, and the cleanup script runs Windows PowerShell, which loads
        /// the library COR_PROFILER_PATH names. A process without rights asks
        /// Windows through the shell's runas verb, and Windows builds the
        /// tool's variables after its prompt.
        /// </summary>
        internal static ProcessStartInfo ElevatedStart(string fileName, string arguments, bool privileged)
            => privileged
                ? new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                }
                : new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    Verb = "runas",
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                };

        /// <summary>
        /// Clean up a temp directory, ignoring errors.
        /// </summary>
        internal static void CleanupTempDir(string tempDir)
        {
            try
            {
                if (Directory.Exists(tempDir))
                    DeleteFolder(tempDir);
            }
            catch { }
        }

        // ─────────────────────────────────────────────
        //  Mad Catz S.T.R.I.K.E. 7 (issue #519)
        // ─────────────────────────────────────────────

        /// <summary>Mad Catz's SDK, strike7_api 1.1, on the server AIDA64's
        /// support points its users at for Strike7API.dll.</summary>
        internal const string Strike7SdkUrl = "https://download.aida64.com/users/strike7_api_1_1.zip";

        /// <summary>The SDK zip's SHA-256, 6,972,426 bytes.</summary>
        internal const string Strike7SdkSha256 = "A54950780B23196659D063643B8D787B5EFA08EB93098354D66FC8C2F591108D";

        /// <summary>The one file PadForge keeps from the SDK.</summary>
        internal const string Strike7SdkLibraryEntry = "STRIKE7 API V1_1/API/x64/Strike7API.dll";

        /// <summary>Microsoft's Visual C++ 2008 SP1 redistributable for x64,
        /// which Strike7API.dll links (MSVCR90 and MSVCP90, bound through its
        /// manifest to Microsoft.VC90.CRT).</summary>
        internal const string Vc2008RedistUrl =
            "https://download.microsoft.com/download/5/D/8/5D8C65CB-C849-4025-8E95-C3966CAFD8AE/vcredist_x64.exe";

        /// <summary>
        /// Downloads Mad Catz's SDK, checks it against its SHA-256, and keeps
        /// only the x64 library, itself checked against its own SHA-256, at
        /// <paramref name="target"/> under the temp folder. PadForge never
        /// ships the library. Runs on a worker thread.
        /// </summary>
        public static void InstallStrike7Library(string target, string librarySha256)
        {
            string staging = StagingFolder("Strike7");
            try
            {
                Directory.CreateDirectory(staging);
                using var http = PadForge.Engine.Common.OutboundNetworkGate.NewHttpClient();
                http.DefaultRequestHeaders.UserAgent.ParseAdd("PadForge");
                http.Timeout = TimeSpan.FromMinutes(10);

                string zip = Path.Combine(staging, "strike7_api_1_1.zip");
                Download(http, Strike7SdkUrl, zip);
                if (!string.Equals(Sha256Hex(zip), Strike7SdkSha256, StringComparison.OrdinalIgnoreCase))
                    throw new DriverCheckException(DriverCheckFailure.Strike7SdkHashMismatch,
                        "The downloaded S.T.R.I.K.E. 7 SDK does not match its known SHA-256.");

                string extracted = Path.Combine(staging, "Strike7API.dll");
                using (var archive = System.IO.Compression.ZipFile.OpenRead(zip))
                {
                    var entry = archive.GetEntry(Strike7SdkLibraryEntry)
                        ?? throw new DriverCheckException(DriverCheckFailure.Strike7SdkHashMismatch,
                            "The S.T.R.I.K.E. 7 SDK holds no " + Strike7SdkLibraryEntry + ".");
                    System.IO.Compression.ZipFileExtensions.ExtractToFile(entry, extracted, overwrite: true);
                }
                if (!string.Equals(Sha256Hex(extracted), librarySha256, StringComparison.OrdinalIgnoreCase))
                    throw new DriverCheckException(DriverCheckFailure.Strike7SdkHashMismatch,
                        "The S.T.R.I.K.E. 7 library in the SDK does not match its known SHA-256.");
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(extracted, target, overwrite: true);
            }
            finally
            {
                CleanupTempDir(staging);
            }
        }

        /// <summary>
        /// Downloads Microsoft's Visual C++ 2008 SP1 redistributable for x64,
        /// checks its Microsoft signature, and runs it quietly
        /// (<c>/q</c>, the switch Microsoft's own deployment notes for the
        /// 2008 package give). The 2008 package takes no /norestart, and 3010
        /// is a restart owed, which IsVcRedistSuccess counts as installed.
        /// Done means the runtime is in the side-by-side store afterward.
        /// Runs on a worker thread.
        /// </summary>
        public static void InstallVc2008Runtime()
        {
            bool installerMayStillRun = false;
            string staging = StagingFolder("Vc2008");
            try
            {
                Directory.CreateDirectory(staging);
                using var http = PadForge.Engine.Common.OutboundNetworkGate.NewHttpClient();
                http.DefaultRequestHeaders.UserAgent.ParseAdd("PadForge");
                http.Timeout = TimeSpan.FromMinutes(10);

                string redist = Path.Combine(staging, "vcredist_x64.exe");
                Download(http, Vc2008RedistUrl, redist);
                if (!IsSignedByMicrosoft(redist))
                    throw new DriverCheckException(DriverCheckFailure.VcRedistUnsigned,
                        "The Visual C++ 2008 Redistributable download is not signed by Microsoft.");
                int? exit = RunElevated(redist, "/q", out bool untracked);
                if (exit == null)
                {
                    installerMayStillRun = true;
                    throw InstallerFailedException.NoExitCode(untracked);
                }
                if (!IsVcRedistSuccess(exit.Value)) throw new InstallerFailedException(exit.Value);
                if (!PadForge.Engine.Common.MadCatz.Strike7Library.IsVc2008RuntimeInstalled())
                    throw new DriverCheckException(DriverCheckFailure.Vc2008RuntimeMissing,
                        "The Visual C++ 2008 runtime is still missing after its installer ran.");
            }
            finally
            {
                if (!installerMayStillRun) CleanupTempDir(staging);
            }
        }

        // ─────────────────────────────────────────────
        //  Novint Falcon (issue #521)
        // ─────────────────────────────────────────────

        /// <summary>The ID Force Dimension's fdhusb.inf binds the Falcon on,
        /// in its Falcon_Install section.</summary>
        internal const string FalconHardwareId = @"USB\VID_0403&PID_CB48";

        /// <summary>
        /// Force Dimension's driver package where its SDK installer leaves it,
        /// <c>drivers\usb\fdhusb.inf</c> under the SDK folder, when the INF
        /// names a catalog and the catalog is beside it. SDK 3.17.7's
        /// installer holds two variants of the INF, and only DriverVer
        /// 3.18.00.03 names fdhusb.cat, signed by Force Dimension. Windows
        /// refuses a third-party INF with no catalog, so PadForge offers no
        /// install of one. Null otherwise.
        /// </summary>
        internal static string FalconDriverPackage(string sdkRoot)
        {
            if (string.IsNullOrWhiteSpace(sdkRoot)) return null;
            try
            {
                string inf = Path.Combine(sdkRoot, "drivers", "usb", "fdhusb.inf");
                return HasCatalog(inf) ? inf : null;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                return null;
            }
        }

        /// <summary>Whether an INF exists and the catalog its [Version]
        /// section names is beside it.</summary>
        internal static bool HasCatalog(string inf)
        {
            if (string.IsNullOrWhiteSpace(inf)) return false;
            try
            {
                if (!File.Exists(inf)) return false;
                string catalog = InfCatalogName(File.ReadAllLines(inf));
                return catalog != null && File.Exists(Path.Combine(Path.GetDirectoryName(inf), catalog));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                          or NotSupportedException or System.Security.SecurityException)
            {
                return false;
            }
        }

        /// <summary>The catalog an INF's [Version] section names in its
        /// CatalogFile entry, or one decorated for x64. Null when it names
        /// none.</summary>
        internal static string InfCatalogName(System.Collections.Generic.IEnumerable<string> infLines)
        {
            bool inVersion = false;
            foreach (string raw in infLines)
            {
                string line = raw;
                int comment = line.IndexOf(';');
                if (comment >= 0) line = line.Substring(0, comment);
                line = line.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("[", StringComparison.Ordinal))
                {
                    inVersion = line.Equals("[Version]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!inVersion) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                if (!key.Equals("CatalogFile", StringComparison.OrdinalIgnoreCase)
                    && !key.Equals("CatalogFile.NT", StringComparison.OrdinalIgnoreCase)
                    && !key.Equals("CatalogFile.NTamd64", StringComparison.OrdinalIgnoreCase))
                    continue;
                string value = line.Substring(eq + 1).Trim().Trim('"');
                if (value.Length > 0) return value;
            }
            return null;
        }

        /// <summary>The interface Force Dimension's package gives a Falcon on
        /// WinUSB, which its library opens the Falcon by. Both INF variants
        /// in SDK 3.17.7 write it (fdhusb-3.18.00.03.inf, Falcon_Install.HW,
        /// and fdhusb-3.7.0.3073.inf, Falcon_AddReg).</summary>
        internal static readonly Guid FalconInterfaceGuid = new("CB5DAA55-DC1A-4f2d-8F76-14CEE032832A");

        /// <summary>Whether a node's DeviceInterfaceGUIDs name Force
        /// Dimension's Falcon interface.</summary>
        internal static bool NamesFalconInterface(System.Collections.Generic.IEnumerable<string> guids)
        {
            if (guids == null) return false;
            foreach (string g in guids)
                if (Guid.TryParse(g, out var guid) && guid == FalconInterfaceGuid) return true;
            return false;
        }

        /// <summary>Whether a Falcon's node is on Force Dimension's own
        /// package: WinUSB, with the interface the package writes to the
        /// node's Device Parameters key. A read of the registry.</summary>
        internal static bool IsOnFalconPackage(PadForge.Services.VendorUsbDriverInstaller.UsbNode node)
        {
            if (!string.Equals(node.Service, "WINUSB", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrEmpty(node.InstanceId))
                return false;
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Enum\" + node.InstanceId + @"\Device Parameters");
                return NamesFalconInterface(key?.GetValue("DeviceInterfaceGUIDs") as string[])
                       || NamesFalconInterface(key?.GetValue("DeviceInterfaceGUID") is string one ? new[] { one } : null);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException
                                          or IOException or ArgumentException)
            {
                return false;
            }
        }

        /// <summary>The Falcons plugged in, as USB nodes with the driver each
        /// one is on.</summary>
        internal static System.Collections.Generic.List<PadForge.Services.VendorUsbDriverInstaller.UsbNode> FalconNodes()
            => PadForge.Services.VendorUsbDriverInstaller.ListPresentUsbNodes()
                .Where(n => n.Vid == PadForge.Common.Input.Falcon.FalconDevice.FalconVendorId
                    && n.Pid == PadForge.Common.Input.Falcon.FalconDevice.FalconProductId).ToList();

        /// <summary>
        /// Installs Force Dimension's own Falcon package, <paramref name="inf"/>
        /// from an SDK folder (<see cref="FalconDriverPackage"/>), onto the
        /// Falcons plugged in, when none of them has a driver. The package is
        /// stocked in the driver store, where Windows may first ask the user to
        /// trust Force Dimension, then forced onto the Falcon's ID, the install
        /// Ds3DriverInstaller and VendorUsbDriverInstaller make. A Falcon on
        /// any driver stops the install, so PadForge never takes a device from
        /// another driver. Done means every Falcon is on WinUSB afterward.
        /// Runs on a worker thread.
        /// </summary>
        public static void InstallFalconDriver(string inf)
        {
            if (OffLimitsForTest)
                throw new UnauthorizedAccessException("The test host changes no driver: " + FalconHardwareId);
            if (!HasCatalog(inf))
                throw new DriverCheckException(DriverCheckFailure.FalconDriverPackageMissing,
                    "Force Dimension's Falcon driver package is missing or has no catalog.", inf ?? string.Empty);
            var nodes = FalconNodes();
            if (nodes.Count == 0)
                throw new DriverCheckException(DriverCheckFailure.FalconNotPluggedIn, "No Falcon is plugged in.");
            foreach (var n in nodes)
                if (!string.IsNullOrEmpty(n.Service))
                    throw new DriverCheckException(DriverCheckFailure.FalconOnAnotherDriver,
                        "A Falcon is on " + n.Service + ", so its driver stays as it is.", n.Service);
            // The install's lines go to the diagnostics ring, which a
            // Release build keeps and Save Snapshot reads, a refused forced
            // bind and a restart Windows asked for among them.
            if (!PadForge.Services.VendorUsbDriverInstaller.InstallSignedPackage(inf, FalconHardwareId,
                    m => PadForge.Engine.SdlDiagLog.WriteLine("Falcon driver: " + m)))
                throw new DriverCheckException(DriverCheckFailure.FalconDriverDeclined,
                    "Windows did not install Force Dimension's driver package.");
            for (int i = 0; i < 20; i++)
            {
                var now = FalconNodes();
                if (now.Count > 0 && now.All(n => string.Equals(n.Service, "WINUSB", StringComparison.OrdinalIgnoreCase)))
                    return;
                System.Threading.Thread.Sleep(250);
            }
            throw new DriverCheckException(DriverCheckFailure.FalconDriverNotBound,
                "The Falcon is not on Force Dimension's driver after the install.");
        }

        // ─────────────────────────────────────────────
        //  What the test host never does
        // ─────────────────────────────────────────────

        /// <summary>Test seam: set by the test assembly for the life of its
        /// process. Nothing here then starts a process, downloads a file,
        /// writes to the registry, or deletes or writes a folder outside the
        /// temp folder: each refuses as a call that failed would, and changes
        /// nothing. Every such call in this class goes through one of the
        /// members below. A test that reaches one with no stand-in in place
        /// (a seam put away too early, a check a change removed) would
        /// otherwise run an installer, an uninstaller or steamcmd on the PC
        /// the tests run on, or delete its SteamVR folder. False in
        /// production, where nothing sets it.</summary>
        internal static bool OffLimitsForTest;

        private const int ERROR_ACCESS_DENIED = 5;

        /// <summary>Starts a process: an installer, an uninstaller, a
        /// bundle's own extractor, pnputil, steamcmd, a cleanup script. In
        /// the test host it starts none and fails as a start Windows
        /// denied.</summary>
        internal static Process StartProcess(ProcessStartInfo psi)
        {
            if (OffLimitsForTest)
                throw new System.ComponentModel.Win32Exception(ERROR_ACCESS_DENIED,
                    "The test host starts no process: " + psi.FileName);
            return PadForge.Services.RightsVariables.Start(psi);
        }

        /// <summary>Fails a download before it begins in the test
        /// host.</summary>
        private static void ThrowIfDownloadOffLimits(string url)
        {
            if (OffLimitsForTest)
                throw new HttpRequestException("The test host downloads nothing: " + url);
        }

        /// <summary>Whether <paramref name="path"/> is inside the temp
        /// folder, where the staging folders of the installs live. The temp
        /// folder itself is not inside it.</summary>
        internal static bool IsInTempFolder(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).TrimEnd('\\', '/').StartsWith(temp, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Deletes a folder with everything in it. In the test host
        /// only a folder inside the temp folder goes, and any other fails as
        /// a delete Windows denied.</summary>
        internal static void DeleteFolder(string path)
        {
            if (OffLimitsForTest && !IsInTempFolder(path))
                throw new UnauthorizedAccessException("The test host deletes no folder outside the temp folder: " + path);
            Directory.Delete(path, recursive: true);
        }

        /// <summary>Deletes a registry key with everything under it, where
        /// it exists. In the test host it deletes none and fails as a write
        /// Windows denied.</summary>
        internal static void DeleteRegistryTree(RegistryKey root, string path)
        {
            if (OffLimitsForTest)
                throw new UnauthorizedAccessException("The test host writes nothing to the registry: " + path);
            root.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }

        /// <summary>Records where the Steam-free SteamVR sits, for
        /// HIDMaestro's discovery. Never from the test host.</summary>
        private static void SetSteamVrPathHint(string dir)
        {
            if (OffLimitsForTest)
                throw new UnauthorizedAccessException("The test host writes nothing to the registry: " + HmRegKeyPath);
            HIDMaestro.HMVR.SetSteamVRPathHint(dir);
        }

        /// <summary>Clears that record once the install is gone. Never from
        /// the test host.</summary>
        private static void ClearSteamVrPathHint()
        {
            if (OffLimitsForTest)
                throw new UnauthorizedAccessException("The test host writes nothing to the registry: " + HmRegKeyPath);
            using var k = Registry.LocalMachine.OpenSubKey(HmRegKeyPath, writable: true);
            k?.DeleteValue(SteamVrPathRegValue, throwOnMissingValue: false);
        }
    }
}
