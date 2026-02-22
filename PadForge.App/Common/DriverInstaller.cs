using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Win32;

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
            var psi = new ProcessStartInfo
            {
                FileName = "msiexec.exe",
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
