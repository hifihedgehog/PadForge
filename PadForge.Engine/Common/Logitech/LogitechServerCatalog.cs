using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace PadForge.Engine.Common.Logitech
{
    /// <summary>
    /// Finds a library Logitech Gaming Software registers by class ID (issue
    /// #519): its LCD library and its Arx Control library.
    ///
    /// <para>Logitech's own static libraries read one key each.
    /// <c>LogitechLCDLib.lib</c> reads
    /// <c>SOFTWARE\Classes\CLSID\{D0E790A5-01A7-49AE-AE0B-E986BDD0C21B}\ServerBinary</c>
    /// inside <c>LogiLcdInit</c>, and <c>LogitechGArxControlLib.lib</c> reads
    /// <c>SOFTWARE\Classes\CLSID\{267CEDC5-DC30-4155-818B-C023A80AEA78}\ServerBinary</c>
    /// in <c>LoadFunctionPointers</c> (the UTF-16 strings in both x64
    /// libraries of the SDK). henninglive/logitech-lcd reads the same class
    /// under HKEY_CLASSES_ROOT and HKEY_LOCAL_MACHINE (logitech-lcd-sys
    /// <c>dll_path_clsid</c>). Every registration is listed, in the order a
    /// caller tries to LOAD them, the G-key catalog's rule.</para>
    ///
    /// <para>logitech-lcd also falls back to the bare name, which Windows
    /// looks for along the PATH. Logitech's own libraries load only the
    /// registered path, and so does this.</para>
    /// </summary>
    public static class LogitechServerCatalog
    {
        public const string LcdClassId = "{D0E790A5-01A7-49AE-AE0B-E986BDD0C21B}";
        public const string ArxClassId = "{267CEDC5-DC30-4155-818B-C023A80AEA78}";

        private const string ServerBinary = "ServerBinary";

        /// <summary>Every path a class's registration names that exists, the
        /// 64-bit registration first. Only the machine's registrations, under
        /// HKEY_LOCAL_MACHINE (#600): HKEY_CLASSES_ROOT lays the user's
        /// classes over them, and the user's programs can write those, so a
        /// registration there would choose the library PadForge loads with
        /// its rights. A value's variables are filled in from the machine's
        /// own environment for the same reason (<see cref="ReadPath"/>).</summary>
        public static IReadOnlyList<string> Registered(string classId)
        {
            var list = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string subKey in new[]
                     {
                         @"SOFTWARE\Classes\CLSID\" + classId + @"\" + ServerBinary,
                         @"SOFTWARE\Classes\Wow6432Node\CLSID\" + classId + @"\" + ServerBinary,
                         @"SOFTWARE\Wow6432Node\Classes\CLSID\" + classId + @"\" + ServerBinary,
                     })
            {
                string path = ReadPath(subKey);
                if (path != null && File.Exists(path) && seen.Add(path)) list.Add(path);
            }
            return list;
        }

        /// <summary>
        /// The library path a registration under HKEY_LOCAL_MACHINE names
        /// (<see cref="PathOf"/>), or null when the key or its value is not
        /// there or cannot be read. The value is read as written: without
        /// RegistryValueOptions.DoNotExpandEnvironmentNames, RegistryKey fills
        /// a REG_EXPAND_SZ value's variables in from this process's
        /// environment (dotnet runtime, RegistryKey.InternalGetValue), which
        /// the user's own variables make.
        /// </summary>
        internal static string ReadPath(string subKey)
        {
            string value = null;
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(subKey);
                value = key?.GetValue(string.Empty, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            }
            catch (Exception)
            {
                // A registry the process cannot read is the same outcome as
                // one with nothing in it.
            }
            return PathOf(value);
        }

        /// <summary>A registered value as the path it names: trimmed, its
        /// quotes taken off, and its variables filled in from the machine's
        /// own values (<see cref="MachineEnvironment.Expand"/>). Null for an
        /// empty value.</summary>
        internal static string PathOf(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            string path = MachineEnvironment.Expand(value.Trim().Trim('"').Trim());
            return path.Length == 0 ? null : path;
        }

        /// <summary>The LCD library's candidates: its registrations.</summary>
        public static IReadOnlyList<string> LcdCandidates() => Registered(LcdClassId);

        /// <summary>The Arx library's candidates: its registrations.</summary>
        public static IReadOnlyList<string> ArxCandidates() => Registered(ArxClassId);
    }
}
