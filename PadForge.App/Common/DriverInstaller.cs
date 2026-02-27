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

        private const string VJoyResourceName = "vJoyDriver.zip";

        private static string GetVJoyTempDir()
            => Path.Combine(Path.GetTempPath(), "PadForge_vJoy");

        /// <summary>
        /// Install vJoy driver. Bypasses the Inno Setup installer entirely:
        /// 1. Extracts the signed driver package (vjoy.sys, hidkmdf.sys,
        ///    vjoy.inf, vjoy.cat, vJoyInterface.dll) from an embedded zip
        ///    to "C:\Program Files\vJoy"
        /// 2. Uses pnputil /add-driver /install to add the driver to the
        ///    Windows driver store (no GUI, no restart dialog)
        /// Device nodes are created on-demand when the user adds a vJoy
        /// controller, via SetupAPI in VJoyVirtualController.CreateVJoyDevice.
        /// All cleanup/install steps run in one elevated batch script (single UAC prompt).
        /// </summary>
        public static void InstallVJoy()
        {
            try
            {
                // Extract the embedded zip to a temp directory.
                var zipPath = ExtractEmbeddedResource(VJoyResourceName, GetVJoyTempDir());

                string vjoyDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "vJoy");
                string vjoyInf = Path.Combine(vjoyDir, "vjoy.inf");

                // Find stale driver store entries before building the script.
                var oemInfs = FindVJoyOemInfs();

                string scriptPath = Path.Combine(Path.GetTempPath(), "PadForge_vjoy_install.cmd");
                string logPath = Path.Combine(Path.GetTempPath(), "PadForge_vjoy_install.log");
                using (var sw = new StreamWriter(scriptPath))
                {
                    sw.WriteLine("@echo off");
                    sw.WriteLine($"echo [%date% %time%] vJoy install starting > \"{logPath}\"");

                    // Step 1: Pre-install cleanup.
                    // IMPORTANT: Remove device nodes FIRST so the driver can fully
                    // unload, THEN stop/delete the service. Reversing this order
                    // causes the service to get stuck in STOP_PENDING.
                    for (int i = 0; i <= 15; i++)
                        sw.WriteLine($"pnputil /remove-device \"ROOT\\HIDCLASS\\{i:D4}\" /subtree >nul 2>&1");
                    sw.WriteLine("timeout /t 2 /nobreak >nul 2>&1");
                    sw.WriteLine("sc stop vjoy >nul 2>&1");
                    sw.WriteLine("timeout /t 2 /nobreak >nul 2>&1");
                    foreach (var inf in oemInfs)
                        sw.WriteLine($"pnputil /delete-driver {inf} /uninstall /force >nul 2>&1");
                    sw.WriteLine($"sc delete vjoy >nul 2>&1");
                    // Fallback: if sc delete failed (e.g. STOP_PENDING), remove the
                    // service registry keys from ALL ControlSets so it doesn't resurrect on reboot.
                    sw.WriteLine("reg delete \"HKLM\\SYSTEM\\CurrentControlSet\\Services\\vjoy\" /f >nul 2>&1");
                    sw.WriteLine("reg delete \"HKLM\\SYSTEM\\ControlSet001\\Services\\vjoy\" /f >nul 2>&1");
                    sw.WriteLine("reg delete \"HKLM\\SYSTEM\\ControlSet002\\Services\\vjoy\" /f >nul 2>&1");
                    sw.WriteLine("reg delete \"HKLM\\SYSTEM\\ControlSet003\\Services\\vjoy\" /f >nul 2>&1");
                    sw.WriteLine($"rmdir /s /q \"{vjoyDir}\" >nul 2>&1");
                    sw.WriteLine("del /f \"%SystemRoot%\\System32\\drivers\\vjoy.sys\" >nul 2>&1");
                    sw.WriteLine($"echo [%time%] Cleanup done >> \"{logPath}\"");

                    // Step 2: Extract driver files from zip.
                    sw.WriteLine($"mkdir \"{vjoyDir}\" >nul 2>&1");
                    sw.WriteLine($"powershell -NoProfile -Command \"Expand-Archive -Path '{zipPath.Replace("'", "''")}' -DestinationPath '{vjoyDir.Replace("'", "''")}' -Force\" >> \"{logPath}\" 2>&1");
                    sw.WriteLine($"echo [%time%] Files extracted >> \"{logPath}\"");

                    // Step 3: Use pnputil to add the driver to the store.
                    // Device nodes are NOT created here — they're created on-demand
                    // by VJoyVirtualController.CreateVJoyDevice using SetupAPI.
                    sw.WriteLine($"pnputil /add-driver \"{vjoyInf}\" /install >> \"{logPath}\" 2>&1");
                    sw.WriteLine($"echo [%time%] pnputil exited with code %errorlevel% >> \"{logPath}\"");
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
        /// Uninstall vJoy driver. Removes device nodes first (so the driver
        /// can unload cleanly), then removes the driver from the store,
        /// deletes the service, and cleans up the install directory.
        /// </summary>
        public static void UninstallVJoy()
        {
            string vjoyDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "vJoy");
            var oemInfs = FindVJoyOemInfs();

            string scriptPath = Path.Combine(Path.GetTempPath(), "PadForge_vjoy_uninstall.cmd");
            string logPath = Path.Combine(Path.GetTempPath(), "PadForge_vjoy_uninstall.log");
            using (var sw = new StreamWriter(scriptPath))
            {
                sw.WriteLine("@echo off");
                sw.WriteLine($"echo [%date% %time%] vJoy uninstall starting > \"{logPath}\"");

                // IMPORTANT: Remove device nodes FIRST so the driver can fully
                // unload from the kernel. If we stop/delete the service while
                // devices are still attached, it gets stuck in STOP_PENDING.
                for (int i = 0; i <= 15; i++)
                    sw.WriteLine($"pnputil /remove-device \"ROOT\\HIDCLASS\\{i:D4}\" /subtree >nul 2>&1");
                sw.WriteLine("timeout /t 2 /nobreak >nul 2>&1");

                // Now stop and delete the service (driver should be unloaded).
                sw.WriteLine("sc stop vjoy >nul 2>&1");
                sw.WriteLine("timeout /t 2 /nobreak >nul 2>&1");
                sw.WriteLine("sc delete vjoy >nul 2>&1");
                // Fallback: if sc delete failed (e.g. STOP_PENDING), remove the
                // service registry keys from ALL ControlSets so it doesn't resurrect on reboot.
                sw.WriteLine("reg delete \"HKLM\\SYSTEM\\CurrentControlSet\\Services\\vjoy\" /f >nul 2>&1");
                sw.WriteLine("reg delete \"HKLM\\SYSTEM\\ControlSet001\\Services\\vjoy\" /f >nul 2>&1");
                sw.WriteLine("reg delete \"HKLM\\SYSTEM\\ControlSet002\\Services\\vjoy\" /f >nul 2>&1");
                sw.WriteLine("reg delete \"HKLM\\SYSTEM\\ControlSet003\\Services\\vjoy\" /f >nul 2>&1");

                // Remove driver from driver store.
                foreach (var inf in oemInfs)
                    sw.WriteLine($"pnputil /delete-driver {inf} /uninstall /force >> \"{logPath}\" 2>&1");

                // Delete the install directory and stale driver binary.
                sw.WriteLine($"rmdir /s /q \"{vjoyDir}\" >nul 2>&1");
                sw.WriteLine("del /f \"%SystemRoot%\\System32\\drivers\\vjoy.sys\" >nul 2>&1");

                // Remove legacy Inno Setup uninstall registry entries (if present).
                sw.WriteLine("powershell -NoProfile -Command \"Get-ChildItem 'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall','HKLM:\\SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall' -EA SilentlyContinue | Where-Object { (Get-ItemProperty $_.PSPath -EA SilentlyContinue).DisplayName -like '*vJoy*' } | Remove-Item -Recurse -Force -EA SilentlyContinue\" >nul 2>&1");

                sw.WriteLine($"echo [%time%] Done >> \"{logPath}\"");
            }

            RunElevated("cmd.exe", $"/c \"{scriptPath}\"");
            try { File.Delete(scriptPath); } catch { }

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
        /// Checks whether the vJoy driver is installed.
        /// Detects both our minimal install (vjoy.sys in Program Files) and
        /// legacy Inno Setup installs (registry uninstall entry).
        /// </summary>
        public static bool IsVJoyInstalled()
        {
            // Primary: check for our minimal driver install.
            string vjoyDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "vJoy");
            if (File.Exists(Path.Combine(vjoyDir, "vjoy.sys")))
                return true;

            // Fallback: check for legacy Inno Setup install via registry.
            return !string.IsNullOrEmpty(GetVJoyVersionFromRegistry());
        }

        /// <summary>
        /// Returns the installed vJoy version string, or null if not found.
        /// </summary>
        public static string GetVJoyVersion()
        {
            // Check our minimal install first.
            string vjoyDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "vJoy");
            if (File.Exists(Path.Combine(vjoyDir, "vjoy.sys")))
            {
                // Try to get version from the driver file.
                try
                {
                    var vi = FileVersionInfo.GetVersionInfo(Path.Combine(vjoyDir, "vjoy.sys"));
                    if (!string.IsNullOrEmpty(vi.FileVersion))
                        return vi.FileVersion;
                }
                catch { }
                return "Installed";
            }

            // Fallback: legacy registry detection.
            return GetVJoyVersionFromRegistry();
        }

        private static string GetVJoyVersionFromRegistry()
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
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var proc = Process.Start(psi);
            proc?.WaitForExit(180_000);
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
