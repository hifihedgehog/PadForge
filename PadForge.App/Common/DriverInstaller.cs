using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Win32;
using PadForge.Common.Input;

namespace PadForge.Common
{
    /// <summary>
    /// Handles installation and uninstallation of ViGEmBus and HidHide drivers
    /// using embedded bootstrapper executables that contain MSI packages.
    /// </summary>
    public static class DriverInstaller
    {
        // ─────────────────────────────────────────────
        //  ViGEmBus
        // ─────────────────────────────────────────────

        private const string ViGEmBusResourceName = "ViGEmBus_1.22.0_x64_x86_arm64.exe";

        private static string GetViGEmBusTempDir()
            => Path.Combine(Path.GetTempPath(), "PadForge_ViGEmBus");

        /// <summary>
        /// Install ViGEmBus driver. Extracts the embedded bootstrapper,
        /// unpacks the MSI, and runs msiexec /i with elevation.
        /// </summary>
        public static void InstallViGEmBus()
        {
            try
            {
                var exePath = ExtractEmbeddedResource(ViGEmBusResourceName, GetViGEmBusTempDir());
                var extractDir = ExtractInstallerBundle(exePath, GetViGEmBusTempDir());
                var msiPath = FindMsi(extractDir,
                    Environment.Is64BitOperatingSystem ? "ViGEmBus.x64.msi" : "ViGEmBus.msi",
                    "ViGEmBus*.msi");

                RunMsiElevated($"/i \"{msiPath}\" /qb /norestart");
            }
            finally
            {
                CleanupTempDir(GetViGEmBusTempDir());
            }
        }

        /// <summary>
        /// Uninstall ViGEmBus driver via msiexec /x with elevation.
        /// </summary>
        public static void UninstallViGEmBus()
        {
            try
            {
                var exePath = ExtractEmbeddedResource(ViGEmBusResourceName, GetViGEmBusTempDir());
                var extractDir = ExtractInstallerBundle(exePath, GetViGEmBusTempDir());
                var msiPath = FindMsi(extractDir,
                    Environment.Is64BitOperatingSystem ? "ViGEmBus.x64.msi" : "ViGEmBus.msi",
                    "ViGEmBus*.msi");

                RunMsiElevated($"/x \"{msiPath}\" /qb /norestart");
            }
            finally
            {
                CleanupTempDir(GetViGEmBusTempDir());
            }
        }

        // ─────────────────────────────────────────────
        //  HidHide
        // ─────────────────────────────────────────────

        private const string HidHideResourceName = "HidHide_1.5.230_x64.exe";

        private static string GetHidHideTempDir()
            => Path.Combine(Path.GetTempPath(), "PadForge_HidHide");

        /// <summary>
        /// Install HidHide driver. Extracts the embedded bootstrapper,
        /// unpacks the MSI, and runs msiexec /i with elevation.
        /// </summary>
        public static void InstallHidHide()
        {
            try
            {
                var exePath = ExtractEmbeddedResource(HidHideResourceName, GetHidHideTempDir());
                var extractDir = ExtractInstallerBundle(exePath, GetHidHideTempDir());
                var msiPath = FindMsi(extractDir, "HidHide.msi", "HidHide*.msi");

                RunMsiElevated($"/i \"{msiPath}\" /qb /norestart");
            }
            finally
            {
                CleanupTempDir(GetHidHideTempDir());
            }
        }

        /// <summary>
        /// Uninstall HidHide driver via msiexec /x with elevation.
        /// </summary>
        public static void UninstallHidHide()
        {
            try
            {
                var exePath = ExtractEmbeddedResource(HidHideResourceName, GetHidHideTempDir());
                var extractDir = ExtractInstallerBundle(exePath, GetHidHideTempDir());
                var msiPath = FindMsi(extractDir, "HidHide.msi", "HidHide*.msi");

                RunMsiElevated($"/x \"{msiPath}\" /qb /norestart");
            }
            finally
            {
                CleanupTempDir(GetHidHideTempDir());
            }
        }

        // ─────────────────────────────────────────────
        //  vJoy
        // ─────────────────────────────────────────────

        private const string VJoyResourceName = "vJoySetup_v2.2.2.0_Win10_Win11.exe";

        private static string GetVJoyTempDir()
            => Path.Combine(Path.GetTempPath(), "PadForge_vJoy");

        private const string VJoySilentArgs = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART";

        /// <summary>
        /// Install vJoy driver. Builds a single elevated batch script that:
        /// 1. Cleans stale driver store entries + leftover install directory
        /// 2. Runs the Inno Setup installer silently
        /// 3. If vJoyInstall.exe fails to bind, creates the vjoy service manually
        ///    and uses pnputil /add-driver /install as a fallback
        /// 4. Creates vJoy device 1 with the layout PadForge needs
        /// All steps run in one elevated process (single UAC prompt).
        /// </summary>
        public static void InstallVJoy()
        {
            try
            {
                var setupExe = ExtractEmbeddedResource(VJoyResourceName, GetVJoyTempDir());

                string vjoyDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "vJoy");
                string vjoyInf = Path.Combine(vjoyDir, "vjoy.inf");
                string vJoyConfigExe = Path.Combine(vjoyDir, "x64", "vJoyConfig.exe");

                // Find stale driver store entries before building the script.
                var oemInfs = FindVJoyOemInfs();

                // Build a batch + PowerShell install script. Single elevated process.
                string scriptPath = Path.Combine(Path.GetTempPath(), "PadForge_vjoy_install.cmd");
                string logPath = Path.Combine(Path.GetTempPath(), "PadForge_vjoy_install.log");
                using (var sw = new StreamWriter(scriptPath))
                {
                    sw.WriteLine("@echo off");
                    sw.WriteLine($"echo [%date% %time%] vJoy install starting > \"{logPath}\"");

                    // Step 1: Pre-install cleanup.
                    foreach (var inf in oemInfs)
                        sw.WriteLine($"pnputil /delete-driver {inf} /uninstall /force >nul 2>&1");
                    sw.WriteLine($"sc delete vjoy >nul 2>&1");
                    sw.WriteLine($"rmdir /s /q \"{vjoyDir}\" >nul 2>&1");
                    sw.WriteLine($"echo [%time%] Cleanup done >> \"{logPath}\"");

                    // Step 2: Run the Inno Setup installer (copies files, vJoyInstall.exe
                    // may fail to bind driver — that's expected on broken systems).
                    sw.WriteLine($"\"{setupExe}\" {VJoySilentArgs}");
                    sw.WriteLine($"echo [%time%] Inno Setup exited with code %errorlevel% >> \"{logPath}\"");

                    // Step 3: Use DiInstallDriver (newdev.dll) to install the driver.
                    // This is a completely different API from UpdateDriverForPlugAndPlayDevices
                    // that vJoyInstall.exe uses. It processes the inf and installs the
                    // driver package for all matching devices in one call.
                    sw.WriteLine($"echo [%time%] Checking vjoy service... >> \"{logPath}\"");
                    sw.WriteLine("sc query vjoy >nul 2>&1");
                    sw.WriteLine($"echo [%time%] sc query vjoy = %errorlevel% >> \"{logPath}\"");
                    sw.WriteLine("if errorlevel 1 (");
                    sw.WriteLine($"  echo [%time%] vjoy service missing, using DiInstallDriver... >> \"{logPath}\"");

                    // Use PowerShell to P/Invoke DiInstallDriver from newdev.dll.
                    // This creates the device node AND binds the driver in one call.
                    string psScript = string.Join("; ",
                        "$sig = '[DllImport(\"newdev.dll\", CharSet=CharSet.Unicode, SetLastError=true)] public static extern bool DiInstallDriverW(IntPtr hwnd, string infPath, uint flags, ref bool reboot);'",
                        "$t = Add-Type -MemberDefinition $sig -Name NativeMethods -Namespace Win32 -PassThru",
                        "$reboot = $false",
                        $"$result = $t::DiInstallDriverW([IntPtr]::Zero, '{vjoyInf.Replace("'", "''")}', 0, [ref]$reboot)",
                        "$err = [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()",
                        "Write-Output \"DiInstallDriver result=$result error=$err reboot=$reboot\"");

                    sw.WriteLine($"  powershell -NoProfile -ExecutionPolicy Bypass -Command \"{psScript.Replace("\"", "\\\"")}\" >> \"{logPath}\" 2>&1");

                    // Fallback: if DiInstallDriver didn't create the service, create it manually.
                    sw.WriteLine($"  sc query vjoy >nul 2>&1");
                    sw.WriteLine($"  if errorlevel 1 (");
                    sw.WriteLine($"    echo [%time%] DiInstallDriver did not create service, creating manually... >> \"{logPath}\"");
                    sw.WriteLine($"    copy /y \"{Path.Combine(vjoyDir, "vjoy.sys")}\" \"%SystemRoot%\\System32\\drivers\\vjoy.sys\" >nul 2>&1");
                    sw.WriteLine($"    copy /y \"{Path.Combine(vjoyDir, "hidkmdf.sys")}\" \"%SystemRoot%\\System32\\drivers\\hidkmdf.sys\" >nul 2>&1");
                    sw.WriteLine("    sc create vjoy type= kernel start= demand error= ignore binPath= \"\\SystemRoot\\System32\\drivers\\vjoy.sys\" DisplayName= \"vJoy Device\"");
                    sw.WriteLine($"    echo [%time%] sc create vjoy = %errorlevel% >> \"{logPath}\"");
                    sw.WriteLine("  )");
                    sw.WriteLine(")");

                    // Step 4: Create vJoy device 1 (6 axes, 11 buttons, 1 discrete POV).
                    sw.WriteLine($"echo [%time%] Running vJoyConfig... >> \"{logPath}\"");
                    sw.WriteLine($"cd /d \"{Path.Combine(vjoyDir, "x64")}\"");
                    sw.WriteLine($"vJoyConfig.exe 1 -f -a x y z rx ry rz -b 11 -s 1 >> \"{logPath}\" 2>&1");
                    sw.WriteLine($"echo [%time%] vJoyConfig exited with code %errorlevel% >> \"{logPath}\"");
                    sw.WriteLine($"echo [%time%] Done >> \"{logPath}\"");
                }

                RunElevated("cmd.exe", $"/c \"{scriptPath}\"");
                try { File.Delete(scriptPath); } catch { }
            }
            finally
            {
                CleanupTempDir(GetVJoyTempDir());
            }
        }

        /// <summary>
        /// Uninstall vJoy driver via its registered uninstaller with /VERYSILENT.
        /// Falls back to the embedded setup if no uninstaller is found.
        /// Cleans leftover driver store entries and registry keys afterward.
        /// </summary>
        public static void UninstallVJoy()
        {
            string uninstallCmd = GetVJoyUninstallString();

            if (!string.IsNullOrEmpty(uninstallCmd))
            {
                // Parse the UninstallString — may be a quoted path with arguments.
                string exe, args;
                if (uninstallCmd.StartsWith("\""))
                {
                    int closeQuote = uninstallCmd.IndexOf('"', 1);
                    exe = uninstallCmd.Substring(1, closeQuote - 1);
                    args = uninstallCmd.Substring(closeQuote + 1).Trim() + " " + VJoySilentArgs;
                }
                else
                {
                    int space = uninstallCmd.IndexOf(' ');
                    if (space > 0)
                    {
                        exe = uninstallCmd.Substring(0, space);
                        args = uninstallCmd.Substring(space + 1).Trim() + " " + VJoySilentArgs;
                    }
                    else
                    {
                        exe = uninstallCmd;
                        args = VJoySilentArgs;
                    }
                }

                RunElevated(exe, args);
            }
            else
            {
                // Fallback: run the embedded installer with silent flags.
                try
                {
                    var exePath = ExtractEmbeddedResource(VJoyResourceName, GetVJoyTempDir());
                    RunElevated(exePath, VJoySilentArgs);
                }
                finally
                {
                    CleanupTempDir(GetVJoyTempDir());
                }
            }

            // Clean leftover registry keys that may cause reinstall hangs.
            // Do NOT use the full CleanVJoyDriverArtifacts() here — removing
            // pnputil entries and files breaks subsequent reinstalls because
            // UpdateDriverForPlugAndPlayDevices can't find the driver.
            CleanVJoyRegistryArtifacts();
        }

        /// <summary>
        /// Light cleanup: removes only registry keys that can cause the vJoy installer
        /// to hang on reinstall. Does NOT touch pnputil or files — those are needed
        /// by the installer to register the driver via UpdateDriverForPlugAndPlayDevices.
        /// </summary>
        private static void CleanVJoyRegistryArtifacts()
        {
            try
            {
                string[] registryPaths =
                {
                    @"SYSTEM\CurrentControlSet\Services\vjoy",
                    @"SYSTEM\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\VID_1234&PID_BEAD",
                    @"SYSTEM\ControlSet001\Services\EventLog\System\vjoy",
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Setup\PnpLockdownFiles\%SystemRoot%/System32/drivers/hidkmdf.sys",
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Setup\PnpLockdownFiles\%SystemRoot%/System32/drivers/vjoy.sys",
                    @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Setup\PnpLockdownFiles\%SystemRoot%/System32/drivers/hidkmdf.sys",
                    @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Setup\PnpLockdownFiles\%SystemRoot%/System32/drivers/vjoy.sys"
                };

                foreach (var path in registryPaths)
                {
                    try { Registry.LocalMachine.DeleteSubKeyTree(path, throwOnMissingSubKey: false); }
                    catch { }
                }

                CleanVJoyDeviceClassEntries();

                try
                {
                    Registry.CurrentUser.DeleteSubKeyTree(
                        @"System\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\VID_1234&PID_BEAD",
                        throwOnMissingSubKey: false);
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
        private static void CleanVJoyDeviceClassEntries()
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
                            Registry.LocalMachine.DeleteSubKeyTree(
                                classPath + @"\" + subName, throwOnMissingSubKey: false);
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
        private static string[] FindVJoyOemInfs()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "pnputil.exe",
                    Arguments = "/enum-drivers",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi);
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
                bool isVJoyBlock = false;

                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();

                    // Detect "Published Name : oemXX.inf" or "Published name: oemXX.inf"
                    if (line.StartsWith("Published", StringComparison.OrdinalIgnoreCase) &&
                        line.Contains(":"))
                    {
                        // Save any previous match.
                        if (isVJoyBlock && currentOem != null)
                            results.Add(currentOem);

                        string value = line.Substring(line.IndexOf(':') + 1).Trim();
                        currentOem = value;
                        isVJoyBlock = false;
                    }
                    // Check if this block mentions Shaul or vjoy.
                    else if (currentOem != null &&
                             (line.IndexOf("shaul", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              line.IndexOf("vjoy", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        isVJoyBlock = true;
                    }
                }

                // Don't forget the last block.
                if (isVJoyBlock && currentOem != null)
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
        /// Checks whether the vJoy driver is installed by scanning the registry.
        /// </summary>
        public static bool IsVJoyInstalled()
        {
            return !string.IsNullOrEmpty(GetVJoyVersion());
        }

        /// <summary>
        /// Returns the installed vJoy version string, or null if not found.
        /// </summary>
        public static string GetVJoyVersion()
        {
            var views = new[] { RegistryView.Registry64, RegistryView.Registry32 };

            foreach (var view in views)
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using var uninstallKey = baseKey.OpenSubKey(
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", false);

                    if (uninstallKey == null) continue;

                    foreach (var subName in uninstallKey.GetSubKeyNames())
                    {
                        using var sub = uninstallKey.OpenSubKey(subName, false);
                        var name = sub?.GetValue("DisplayName") as string;
                        if (string.IsNullOrEmpty(name)) continue;

                        if (name.IndexOf("vJoy", StringComparison.OrdinalIgnoreCase) < 0)
                            continue;

                        return sub.GetValue("DisplayVersion") as string ?? "Installed";
                    }
                }
                catch { }
            }

            return null;
        }

        private static string GetVJoyUninstallString()
        {
            var views = new[] { RegistryView.Registry64, RegistryView.Registry32 };

            foreach (var view in views)
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using var uninstallKey = baseKey.OpenSubKey(
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", false);

                    if (uninstallKey == null) continue;

                    foreach (var subName in uninstallKey.GetSubKeyNames())
                    {
                        using var sub = uninstallKey.OpenSubKey(subName, false);
                        var name = sub?.GetValue("DisplayName") as string;
                        if (string.IsNullOrEmpty(name)) continue;

                        if (name.IndexOf("vJoy", StringComparison.OrdinalIgnoreCase) < 0)
                            continue;

                        return sub.GetValue("UninstallString") as string;
                    }
                }
                catch { }
            }

            return null;
        }

        // ─────────────────────────────────────────────
        //  ViGEmBus detection
        // ─────────────────────────────────────────────

        /// <summary>
        /// Returns the installed ViGEmBus version string, or null if not found in registry.
        /// </summary>
        public static string GetViGEmVersion()
        {
            var views = new[] { RegistryView.Registry64, RegistryView.Registry32 };

            foreach (var view in views)
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using var uninstallKey = baseKey.OpenSubKey(
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", false);

                    if (uninstallKey == null) continue;

                    foreach (var subName in uninstallKey.GetSubKeyNames())
                    {
                        using var sub = uninstallKey.OpenSubKey(subName, false);
                        var name = sub?.GetValue("DisplayName") as string;
                        if (string.IsNullOrEmpty(name)) continue;

                        if (name.IndexOf("ViGEm", StringComparison.OrdinalIgnoreCase) < 0)
                            continue;

                        return sub.GetValue("DisplayVersion") as string;
                    }
                }
                catch { }
            }

            return null;
        }

        // ─────────────────────────────────────────────
        //  HidHide detection
        // ─────────────────────────────────────────────

        /// <summary>
        /// Checks whether HidHide is installed by scanning the registry Uninstall keys.
        /// </summary>
        public static bool IsHidHideInstalled()
        {
            return TryGetHidHideMsiInfo(out _, out _);
        }

        /// <summary>
        /// Returns the installed HidHide version string, or null if not installed.
        /// </summary>
        public static string GetHidHideVersion()
        {
            if (!TryGetHidHideMsiInfo(out var version, out _))
                return null;
            return string.IsNullOrEmpty(version) ? "Installed" : version;
        }

        private static bool TryGetHidHideMsiInfo(out string displayVersion, out string productCode)
        {
            displayVersion = null;
            productCode = null;

            var views = new[] { RegistryView.Registry64, RegistryView.Registry32 };

            foreach (var view in views)
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using var uninstallKey = baseKey.OpenSubKey(
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", false);

                    if (uninstallKey == null) continue;

                    foreach (var subName in uninstallKey.GetSubKeyNames())
                    {
                        using var sub = uninstallKey.OpenSubKey(subName, false);
                        var name = sub?.GetValue("DisplayName") as string;
                        if (string.IsNullOrEmpty(name)) continue;

                        if (name.IndexOf("HidHide", StringComparison.OrdinalIgnoreCase) < 0 &&
                            name.IndexOf("HID Hide", StringComparison.OrdinalIgnoreCase) < 0)
                            continue;

                        displayVersion = sub.GetValue("DisplayVersion") as string;

                        if (subName.StartsWith("{") && subName.EndsWith("}"))
                            productCode = subName;

                        return true;
                    }
                }
                catch
                {
                    // Ignore and try the other registry view.
                }
            }

            return false;
        }

        // ─────────────────────────────────────────────
        //  Shared helpers
        // ─────────────────────────────────────────────

        /// <summary>
        /// Extract an embedded resource to a temp directory.
        /// Returns the full path to the extracted file.
        /// </summary>
        private static string ExtractEmbeddedResource(string resourceFileName, string tempDir)
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
                Directory.Delete(extractDir, true);

            Directory.CreateDirectory(extractDir);

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"/extract \"{extractDir}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (var proc = Process.Start(psi))
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
        /// Run msiexec.exe with elevation (UAC prompt).
        /// </summary>
        private static void RunMsiElevated(string arguments)
        {
            RunElevated("msiexec.exe", arguments);
        }

        /// <summary>
        /// Run an executable with elevation (UAC prompt).
        /// </summary>
        private static void RunElevated(string fileName, string arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                Verb = "runas",
                UseShellExecute = true
            };

            using var proc = Process.Start(psi);
            proc?.WaitForExit(120_000);
        }

        /// <summary>
        /// Clean up a temp directory, ignoring errors.
        /// </summary>
        private static void CleanupTempDir(string tempDir)
        {
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, true);
            }
            catch { }
        }
    }
}
