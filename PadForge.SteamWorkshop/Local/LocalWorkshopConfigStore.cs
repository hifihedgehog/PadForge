using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using PadForge.SteamWorkshop.Vdf;

namespace PadForge.SteamWorkshop.Local
{
    /// <summary>
    /// Read-only access to Steam Input configs that a local Steam client has already
    /// downloaded via a Workshop subscription. Subscribed controller configs land in
    /// <c>{library}\steamapps\workshop\content\241100\{publishedfileid}\</c> in every
    /// Steam library, as either <c>controller_configuration.vdf</c> (SteamPipe-manifest
    /// items) or <c>{ugchandle}_legacy.bin</c> (pre-manifest UGC items), and both
    /// payloads are text VDF. This is the offline path for legacy configs whose
    /// <c>file_url</c> is absent or dead. Nothing here touches the network and nothing
    /// is ever written.
    /// </summary>
    public static class LocalWorkshopConfigStore
    {
        /// <summary>The Workshop bucket Steam files all controller configs under
        /// ("Steam Controller Configs" on SteamDB).</summary>
        public const int ControllerConfigsAppId = 241100;

        /// <summary>
        /// Finds and reads the local copy of a subscribed config. Null when no Steam
        /// install is present, no library holds the item, or the file is unreadable
        /// or over the VDF parser's input cap.
        /// </summary>
        public static string ReadConfigText(ulong publishedFileId) =>
            ReadConfigText(publishedFileId, GetSteamLibraryRoots());

        /// <summary>Testable overload probing only the given library roots.</summary>
        public static string ReadConfigText(ulong publishedFileId, IEnumerable<string> steamLibraryRoots)
        {
            var path = FindConfigFile(publishedFileId, steamLibraryRoots);
            if (path == null) return null;

            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > VdfParser.MaxInputBytes) return null;
                return File.ReadAllText(path);
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>
        /// The on-disk payload for a subscribed config, probing each library root in
        /// order, or null. Prefers the canonical <c>controller_configuration.vdf</c>,
        /// falling back to the item's <c>*_legacy.bin</c>.
        /// </summary>
        public static string FindConfigFile(ulong publishedFileId, IEnumerable<string> steamLibraryRoots)
        {
            if (steamLibraryRoots == null) return null;

            foreach (var root in steamLibraryRoots)
            {
                if (string.IsNullOrWhiteSpace(root)) continue;

                var dir = Path.Combine(root, "steamapps", "workshop", "content",
                    ControllerConfigsAppId.ToString(CultureInfo.InvariantCulture),
                    publishedFileId.ToString(CultureInfo.InvariantCulture));
                if (!Directory.Exists(dir)) continue;

                var canonical = Path.Combine(dir, "controller_configuration.vdf");
                if (File.Exists(canonical)) return canonical;

                string legacy = null;
                try
                {
                    // Ordinal-smallest for determinism. Observed folders hold a single payload.
                    foreach (var file in Directory.EnumerateFiles(dir, "*_legacy.bin"))
                    {
                        if (legacy == null || string.CompareOrdinal(file, legacy) < 0)
                            legacy = file;
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
                if (legacy != null) return legacy;
            }

            return null;
        }

        /// <summary>
        /// Every local Steam library root (install dir first), from the registry install
        /// path plus <c>libraryfolders.vdf</c>. Empty when Steam is not installed.
        /// </summary>
        public static IReadOnlyList<string> GetSteamLibraryRoots()
        {
            var roots = new List<string>();
            var steamRoot = ReadSteamInstallPath();
            if (string.IsNullOrWhiteSpace(steamRoot)) return roots;
            AddRoot(roots, steamRoot);

            // steamapps\libraryfolders.vdf is the canonical manifest. Older clients
            // kept it under config\. Both carry the same list, so first readable wins.
            foreach (var manifest in new[]
                     {
                         Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf"),
                         Path.Combine(steamRoot, "config", "libraryfolders.vdf"),
                     })
            {
                var text = TryReadSmallFile(manifest);
                if (text == null) continue;
                foreach (var library in ParseLibraryFolders(text))
                    AddRoot(roots, library);
                break;
            }

            return roots;
        }

        /// <summary>
        /// Library paths out of a <c>libraryfolders.vdf</c> document. Handles both the
        /// current shape (numeric key to an object with a <c>path</c>) and the pre-2021
        /// flat shape (numeric key directly to the path string). Non-numeric keys such
        /// as <c>TimeNextStatsReport</c> are metadata, not libraries.
        /// </summary>
        public static IReadOnlyList<string> ParseLibraryFolders(string libraryFoldersVdf)
        {
            var paths = new List<string>();
            if (string.IsNullOrEmpty(libraryFoldersVdf)) return paths;
            if (!VdfParser.TryParse(libraryFoldersVdf, out var doc, out _)) return paths;

            foreach (var kv in doc["libraryfolders"].Children)
            {
                if (kv.Key.Length == 0 || !kv.Key.All(char.IsAsciiDigit)) continue;
                var path = kv.Value.IsObject ? kv.Value["path"].AsString : kv.Value.AsString;
                if (!string.IsNullOrWhiteSpace(path))
                    paths.Add(path);
            }

            return paths;
        }

        private static string ReadSteamInstallPath()
        {
            try
            {
                // Per-user value first (forward-slash form, e.g. "c:/program files (x86)/steam"),
                // then the machine-wide installer value.
                var user = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
                if (!string.IsNullOrWhiteSpace(user)) return user;

                return Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or IOException)
            {
                return null;
            }
        }

        private static void AddRoot(List<string> roots, string candidate)
        {
            string normalized;
            try
            {
                normalized = Path.GetFullPath(candidate);
            }
            catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
            {
                return;
            }

            if (!roots.Any(r => string.Equals(r, normalized, StringComparison.OrdinalIgnoreCase)))
                roots.Add(normalized);
        }

        private static string TryReadSmallFile(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > VdfParser.MaxInputBytes) return null;
                return File.ReadAllText(path);
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }
    }
}
