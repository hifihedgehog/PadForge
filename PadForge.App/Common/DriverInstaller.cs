using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
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
        /// Uninstall ViGEmBus driver via msiexec /x with elevation. Retained
        /// in v3 only for the first-run cleanup wizard that removes legacy
        /// v2 driver installs from upgrading users' systems. Will be removed
        /// after the migration window.
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
        //  Windows MIDI Services
        // ─────────────────────────────────────────────

        // Note: /releases/latest returns 404 because microsoft/MIDI only publishes
        // pre-releases. Use /releases (returns array) and parse the first entry.
        private const string MidiServicesGitHubApi =
            "https://api.github.com/repos/microsoft/MIDI/releases";

        private static string GetMidiServicesTempDir()
            => Path.Combine(Path.GetTempPath(), "PadForge_MidiServices");

        /// <summary>
        /// Downloads and runs the latest Windows MIDI Services SDK Runtime installer.
        /// Uses the GitHub API to find the latest release asset dynamically.
        /// The installer is ~210MB so it must be downloaded rather than embedded.
        /// </summary>
        public static async Task InstallMidiServicesAsync()
        {
            var tempDir = GetMidiServicesTempDir();
            Directory.CreateDirectory(tempDir);

            var installerPath = Path.Combine(tempDir, "MidiServicesSdkRuntime.exe");
            try
            {
                using var http = new HttpClient();
                http.DefaultRequestHeaders.UserAgent.ParseAdd("PadForge");
                http.Timeout = TimeSpan.FromMinutes(10);

                // Query GitHub API for the latest release and find the SDK Runtime installer asset.
                var downloadUrl = await FindMidiServicesDownloadUrl(http);

                using var response = await http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                using var stream = await response.Content.ReadAsStreamAsync();
                using var fs = new FileStream(installerPath, FileMode.Create, FileAccess.Write);
                await stream.CopyToAsync(fs);

                // Run the WiX Burn bootstrapper. PadForge is already elevated,
                // so run directly (no runas) to avoid Win32Exception on some systems.
                // Close the file stream before launching the installer.
                fs.Close();
                var psi = new ProcessStartInfo
                {
                    FileName = installerPath,
                    Arguments = "/install /quiet /norestart",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit(300_000); // 5 minutes — Burn bundles can take a while

                // Reset the cached availability check so IsAvailable() re-evaluates.
                MidiVirtualController.ResetAvailability();
            }
            finally
            {
                CleanupTempDir(tempDir);
            }
        }

        /// <summary>
        /// Queries the GitHub API for the latest microsoft/MIDI release and returns
        /// the download URL for the SDK Runtime x64 installer asset.
        /// </summary>
        private static async Task<string> FindMidiServicesDownloadUrl(HttpClient http)
        {
            var json = await http.GetStringAsync(MidiServicesGitHubApi);

            // Simple JSON parsing — find the browser_download_url for the SDK Runtime x64 exe.
            // Asset name pattern: "Windows.MIDI.Services.SDK.Runtime.and.Tools.*-x64.exe"
            const string needle = "browser_download_url";
            int pos = 0;
            while ((pos = json.IndexOf(needle, pos, StringComparison.Ordinal)) >= 0)
            {
                // Find the URL value after the key.
                int urlStart = json.IndexOf("\"http", pos, StringComparison.Ordinal);
                if (urlStart < 0) break;
                urlStart++; // skip opening quote
                int urlEnd = json.IndexOf('"', urlStart);
                if (urlEnd < 0) break;

                string url = json.Substring(urlStart, urlEnd - urlStart);
                if (url.Contains("SDK.Runtime", StringComparison.OrdinalIgnoreCase) &&
                    url.Contains("x64", StringComparison.OrdinalIgnoreCase) &&
                    url.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    return url;
                }

                pos = urlEnd;
            }

            throw new InvalidOperationException(
                "Could not find Windows MIDI Services SDK Runtime installer in the latest GitHub release.");
        }

        /// <summary>
        /// Uninstalls Windows MIDI Services by finding the cached WiX Burn bootstrapper
        /// via the registry UninstallString and launching it with /uninstall /quiet.
        /// The uninstaller is launched fire-and-forget because the MIDI Services SDK
        /// DLLs are loaded in-process — waiting for the uninstaller to finish would
        /// cause a native crash when the backing service is removed mid-session.
        /// </summary>
        public static void UninstallMidiServices()
        {
            string uninstallCmd = FindMidiServicesUninstallString();
            if (string.IsNullOrEmpty(uninstallCmd))
                throw new InvalidOperationException("Could not find Windows MIDI Services uninstall entry in registry.");

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
                Arguments = $"{existingArgs} /quiet /norestart".Trim(),
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(300_000);
        }

        /// <summary>
        /// Searches the registry Uninstall keys for the Windows MIDI Services entry
        /// and returns its UninstallString value.
        /// </summary>
        private static string FindMidiServicesUninstallString()
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

                        // Match the WiX Burn bootstrapper bundle entry, not the individual MSI components.
                        if (name.Equals("Windows MIDI Services Runtime and Tools", StringComparison.OrdinalIgnoreCase))
                        {
                            return sub.GetValue("UninstallString") as string;
                        }
                    }
                }
                catch { }
            }

            return null;
        }

        /// <summary>
        /// Checks whether Windows MIDI Services is installed by looking for the
        /// registry uninstall entry. Does NOT load the SDK runtime — that would
        /// lock native DLLs in-process and prevent clean uninstallation.
        /// </summary>
        public static bool IsMidiServicesInstalled()
        {
            return FindMidiServicesUninstallString() != null;
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
