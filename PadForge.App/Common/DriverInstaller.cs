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
            RunMsiElevated($"/x {productCode} /qb /norestart");
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
                        if (name.IndexOf(displayNameSubstring, StringComparison.OrdinalIgnoreCase) < 0) continue;

                        // Only return MSI-format ProductCode GUIDs; fall through
                        // for Inno/NSIS-style installers that live elsewhere.
                        if (subName.StartsWith("{") && subName.EndsWith("}"))
                            return subName;
                    }
                }
                catch { }
            }
            return null;
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

            string scriptPath = Path.Combine(Path.GetTempPath(), "PadForge_vjoy_uninstall.cmd");
            using (var sw = new StreamWriter(scriptPath))
            {
                sw.WriteLine("@echo off");

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
                    sw.WriteLine($"pnputil /delete-driver {inf} /uninstall /force >nul 2>&1");

                // Delete the install directory and stale driver binary.
                sw.WriteLine($"rmdir /s /q \"{vjoyDir}\" >nul 2>&1");
                sw.WriteLine("del /f \"%SystemRoot%\\System32\\drivers\\vjoy.sys\" >nul 2>&1");

                // Remove legacy Inno Setup uninstall registry entries (if present).
                sw.WriteLine("powershell -NoProfile -Command \"Get-ChildItem 'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall','HKLM:\\SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall' -EA SilentlyContinue | Where-Object { (Get-ItemProperty $_.PSPath -EA SilentlyContinue).DisplayName -like '*vJoy*' } | Remove-Item -Recurse -Force -EA SilentlyContinue\" >nul 2>&1");
            }

            RunElevated("cmd.exe", $"/c \"{scriptPath}\"");
            try { File.Delete(scriptPath); } catch { }

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
                    try { Registry.LocalMachine.DeleteSubKeyTree(path, throwOnMissingSubKey: false); }
                    catch { }
                }

                CleanExtendedDeviceClassEntries();

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
        private static string[] FindExtendedOemInfs()
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
                        line, @"oem\d+\.inf",
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

        private static string GetSteamVrTempDir()
            => Path.Combine(Path.GetTempPath(), "PadForge_SteamCmd");

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
                ?? throw new InvalidOperationException(
                    "No PadForge-owned SteamVR install to remove.");
            if (Process.GetProcessesByName("vrserver").Length > 0)
                throw new InvalidOperationException("SteamVR is running.");
            // Independent of the install-side refusal: the hint is a plain
            // registry value anyone can edit, and this is the line that
            // recursively deletes whatever it names.
            if (IsDriveRoot(dir))
                throw new InvalidOperationException(
                    "The recorded SteamVR path is a drive root; refusing to delete it.");

            Directory.Delete(dir, recursive: true);

            using (var k = Registry.LocalMachine.OpenSubKey(HmRegKeyPath, writable: true))
                k?.DeleteValue(SteamVrPathRegValue, throwOnMissingValue: false);

            HMaestroVRController.ResetAvailability();
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
                throw new ArgumentException(
                    "The SteamVR install location must be a full path, e.g. C:\\SteamVR.");
            // A drive root is never a legitimate target, and allowing one
            // would arm the uninstall side with Directory.Delete on the
            // whole drive. Refuse it here too, symmetrically.
            if (IsDriveRoot(targetDir))
                throw new ArgumentException(
                    "The SteamVR install location cannot be a drive root; pick a folder, e.g. C:\\SteamVR.");
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
                    using var proc = Process.Start(psi);
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
                        throw new TimeoutException("steamcmd did not finish within 60 minutes.");
                    }
                    string outText = await stdoutTask;
                    lastTail = outText.Length > 400 ? outText[^400..] : outText;
                }

                if (!File.Exists(vrpathreg))
                    throw new InvalidOperationException(
                        "steamcmd ran but SteamVR did not land in " + targetDir
                        + ". steamcmd said: ..." + lastTail);

                // Steam-free installs write no registry keys of their own;
                // the hint makes discovery unconditional. Requires admin,
                // which PadForge always has.
                HIDMaestro.HMVR.SetSteamVRPathHint(targetDir);
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
