using System;
using System.Collections.Generic;
using System.IO;

namespace PadForge.Engine.Common.Logitech
{
    /// <summary>
    /// Finds <c>LogitechGkey.dll</c> (issue #454).
    ///
    /// <para>Registry first, default install path second, which is the order
    /// Mumble's <c>GKey.cpp</c> has used in production for years. The registry
    /// matters for two reasons: it finds an install that is not in Program
    /// Files, and it answers "is this SDK on this machine" as a fact instead
    /// of a guess about which Logitech software the user runs.</para>
    ///
    /// <para>The SDK ships with Logitech Gaming Software. G HUB, which
    /// replaced it, carries legacy libraries for lighting, ARX and steering
    /// wheels, and no public source shows it carrying a G-key one. Rather than
    /// encode that as a rule, this looks for the library and reports what it
    /// found, so a machine that does have it works whatever put it there.</para>
    /// </summary>
    public static class LogitechGKeyCatalog
    {
        /// <summary>The SDK's registered CLSID. Its ServerBinary value is the
        /// DLL's path.</summary>
        public const string ClassId = "{7bded654-f278-4977-a20f-6e72a0d07859}";

        private const string ServerBinary = "ServerBinary";

        /// <summary>Why no library was found, for the status line.</summary>
        public enum Reason
        {
            Found = 0,
            RegistryPathMissing,
            NotInstalled,
        }

        /// <summary>
        /// The library's path, or null with a reason.
        ///
        /// <para>A registry value pointing at a file that is no longer there
        /// is reported as its own case, because it means the Logitech software
        /// was uninstalled and left the class behind, which is a different
        /// thing for a user to fix than never having installed it.</para>
        /// </summary>
        public static string Find(out Reason reason)
        {
            var found = Candidates(out reason);
            return found.Count > 0 ? found[0] : null;
        }

        /// <summary>
        /// Every library on this machine that could be the SDK, registered
        /// ones first, in the order a caller should try to LOAD them.
        ///
        /// <para>Existing and loading are different questions. A registry
        /// entry can name the x86 build, which is present and will not load
        /// into an x64 process, and the x64 build sits at the default path
        /// the caller never reached. Mumble's GKey.cpp builds the same list
        /// and breaks on the first that loads, not the first that exists.</para>
        /// </summary>
        public static IReadOnlyList<string> Candidates(out Reason reason)
        {
            var list = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool registryPathMissing = false;

            foreach (string path in RegistryCandidates())
            {
                if (File.Exists(path))
                {
                    if (seen.Add(path)) list.Add(path);
                }
                else registryPathMissing = true;
            }

            foreach (string path in DefaultCandidates())
            {
                if (!File.Exists(path)) continue;
                if (seen.Add(path)) list.Add(path);
            }

            // A registration whose file is gone is its own case only when
            // nothing else turned up, since it tells the user to reinstall
            // rather than to install.
            reason = list.Count > 0 ? Reason.Found
                   : registryPathMissing ? Reason.RegistryPathMissing
                   : Reason.NotInstalled;
            return list;
        }

        /// <summary>
        /// Paths the registered class points at.
        ///
        /// <para>The three machine views of the class are read rather than
        /// only the one matching this process: the 32-bit registration's two
        /// spellings under Wow6432Node, where the SDK registers itself, and
        /// the 64-bit one, so a machine that has only one of them is
        /// covered.</para>
        ///
        /// <para>Only the machine's own registration is read, under
        /// HKEY_LOCAL_MACHINE (#600). HKEY_CLASSES_ROOT lays the user's
        /// classes over it, and the user's programs can write those, so a
        /// registration there would choose the library PadForge loads with
        /// its rights. The SDK's installer runs with rights and registers
        /// under the machine. Each value is read by the rule the LCD and Arx
        /// libraries' are read by (<see cref="LogitechServerCatalog.ReadPath"/>):
        /// its quotes taken off and its variables filled in from the
        /// machine's own environment.</para>
        /// </summary>
        private static IEnumerable<string> RegistryCandidates()
        {
            foreach (string subKey in new[]
                     {
                         @"SOFTWARE\Classes\Wow6432Node\CLSID\" + ClassId + @"\" + ServerBinary,
                         @"SOFTWARE\Wow6432Node\Classes\CLSID\" + ClassId + @"\" + ServerBinary,
                         @"SOFTWARE\Classes\CLSID\" + ClassId + @"\" + ServerBinary,
                     })
            {
                string path = LogitechServerCatalog.ReadPath(subKey);
                if (path != null) yield return path;
            }
        }

        /// <summary>Where the installer puts it when nothing redirected it.</summary>
        private static IEnumerable<string> DefaultCandidates()
        {
            foreach (var folder in new[]
                     {
                         Environment.SpecialFolder.ProgramFiles,
                         Environment.SpecialFolder.ProgramFilesX86,
                     })
            {
                string root;
                try { root = Environment.GetFolderPath(folder); }
                catch (Exception) { continue; }
                if (string.IsNullOrEmpty(root)) continue;

                // The SDK ships both builds. Ours has to match the process, and
                // PadForge is x64, so that one is tried first.
                yield return Path.Combine(root, "Logitech Gaming Software", "SDK", "G-key", "x64", "LogitechGkey.dll");
                yield return Path.Combine(root, "Logitech Gaming Software", "SDK", "G-key", "x86", "LogitechGkey.dll");
            }
        }
    }
}
