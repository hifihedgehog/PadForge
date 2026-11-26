using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace PadForge.Engine.Common.ForceDimension
{
    /// <summary>
    /// Finds Force Dimension's haptic library, <c>dhd64.dll</c>, which opens a
    /// Novint Falcon (issue #521). PadForge never ships it: Force Dimension's
    /// license grants use and no right to redistribute
    /// (<c>doc/licenses/license.txt</c> in SDK 3.17.7), so the copy the user
    /// installed is the one loaded.
    ///
    /// <para>Force Dimension's installer copies the library to the SDK's
    /// <c>bin</c> folder and to System32. CHAI3D loads it by bare name,
    /// <c>LoadLibrary("dhd64.dll")</c> (<c>src/devices/CDeltaDevices.cpp</c>),
    /// which finds the System32 copy. This lists full paths instead, every
    /// one that exists, in the order a caller tries to load them: a copy
    /// the user picked, the <c>FDSDK</c> folder that Force Dimension's Python
    /// binding has users set to the SDK's root, the folder the installer
    /// recorded in its uninstall entry, each <c>sdk-*</c> folder under
    /// <c>%ProgramFiles%\Force Dimension</c> or <c>ForceDimension</c> newest
    /// first, and
    /// System32. Existing and loading are different questions, the G-key
    /// catalog's rule, so the caller tries each until one loads.</para>
    /// </summary>
    public static class ForceDimensionCatalog
    {
        public const string LibraryName = "dhd64.dll";

        /// <summary>Every candidate that exists, in load order.</summary>
        public static IReadOnlyList<string> Candidates(string picked)
        {
            var list = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(string path)
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                try
                {
                    path = Path.GetFullPath(path);
                    if (File.Exists(path) && seen.Add(path)) list.Add(path);
                }
                catch (Exception)
                {
                    // A malformed path is no candidate.
                }
            }

            Add(picked);
            foreach (string root in SdkRoots())
            {
                Add(Path.Combine(root, "bin", LibraryName));
                Add(Path.Combine(root, "lib", LibraryName));
            }
            try { Add(Path.Combine(Environment.SystemDirectory, LibraryName)); }
            catch (Exception) { }
            return list;
        }

        /// <summary>The SDK folders this PC names: <c>FDSDK</c>, the
        /// installer's uninstall entries, then each <c>sdk-*</c> folder under
        /// Program Files, newest version first.</summary>
        public static IReadOnlyList<string> SdkRoots()
        {
            var roots = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(string root)
            {
                if (string.IsNullOrWhiteSpace(root)) return;
                try
                {
                    root = Path.GetFullPath(MachineEnvironment.Expand(root.Trim().Trim('"')));
                    if (Directory.Exists(root) && seen.Add(root)) roots.Add(root);
                }
                catch (Exception) { }
            }

            // The machine's FDSDK, which the SDK's installer sets, never the
            // process's (#600): the process's comes from the user's part of the
            // registry, which the user's programs can write, and would choose
            // the library PadForge loads with its rights.
            Add(MachineEnvironment.Get("FDSDK"));
            foreach (string folder in UninstallLocations()) Add(folder);

            // FalconJoyForce's README names the folder without the space
            // ("C:\Program Files\ForceDimension\sdk-X.X.X"), so both
            // spellings are looked in.
            var versioned = new List<(Version Version, string Path)>();
            foreach (var special in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            foreach (string vendor in new[] { "Force Dimension", "ForceDimension" })
            {
                string parent;
                try { parent = Path.Combine(Environment.GetFolderPath(special), vendor); }
                catch (Exception) { continue; }
                if (!Directory.Exists(parent)) continue;
                try
                {
                    foreach (string dir in Directory.EnumerateDirectories(parent, "sdk-*"))
                    {
                        string name = Path.GetFileName(dir);
                        Version.TryParse(name.Substring(4), out var version);
                        versioned.Add((version ?? new Version(0, 0), dir));
                    }
                }
                catch (Exception) { }
            }
            versioned.Sort((a, b) => b.Version.CompareTo(a.Version));
            foreach (var (_, path) in versioned) Add(path);
            return roots;
        }

        /// <summary>The install folders Force Dimension's Inno Setup
        /// installers recorded under the uninstall key, from both registry
        /// views.</summary>
        private static IEnumerable<string> UninstallLocations()
        {
            var found = new List<string>();
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using var uninstall = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                    if (uninstall == null) continue;
                    foreach (string name in uninstall.GetSubKeyNames())
                    {
                        using var entry = uninstall.OpenSubKey(name);
                        if (entry?.GetValue("DisplayName") is not string display
                            || display.IndexOf("Force Dimension", StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        if (entry.GetValue("InstallLocation") is string location && location.Length > 0)
                            found.Add(location);
                        else if (entry.GetValue("Inno Setup: App Path") is string app && app.Length > 0)
                            found.Add(app);
                    }
                }
                catch (Exception)
                {
                    // A registry the process cannot read is the same outcome
                    // as one with nothing in it.
                }
            }
            return found;
        }

        /// <summary>The SDK folder a library was loaded from: the parent of
        /// its <c>bin</c> or <c>lib</c> folder, where the installer keeps
        /// <c>drivers\usb\fdhusb.inf</c>. Null for a copy outside an SDK
        /// folder, System32's among them.</summary>
        public static string SdkRootOf(string libraryPath)
        {
            if (string.IsNullOrEmpty(libraryPath)) return null;
            try
            {
                string dir = Path.GetDirectoryName(libraryPath);
                string leaf = Path.GetFileName(dir);
                if (!leaf.Equals("bin", StringComparison.OrdinalIgnoreCase)
                    && !leaf.Equals("lib", StringComparison.OrdinalIgnoreCase))
                    return null;
                return Path.GetDirectoryName(dir);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
