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
        /// HKEY_LOCAL_MACHINE (#597): HKEY_CLASSES_ROOT lays the user's
        /// classes over them, and the user's programs can write those, so a
        /// registration there would choose the library PadForge loads with
        /// its rights. A value's variables are filled in from the machine's
        /// own environment for the same reason.</summary>
        public static IReadOnlyList<string> Registered(string classId)
        {
            var list = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (hive, subKey) in new[]
                     {
                         (Registry.LocalMachine, @"SOFTWARE\Classes\CLSID\" + classId + @"\" + ServerBinary),
                         (Registry.LocalMachine, @"SOFTWARE\Classes\Wow6432Node\CLSID\" + classId + @"\" + ServerBinary),
                         (Registry.LocalMachine, @"SOFTWARE\Wow6432Node\Classes\CLSID\" + classId + @"\" + ServerBinary),
                     })
            {
                string value = null;
                try
                {
                    using var key = hive.OpenSubKey(subKey);
                    value = key?.GetValue(string.Empty) as string;
                }
                catch (Exception)
                {
                    // A registry the process cannot read is the same outcome
                    // as one with nothing in it.
                }
                if (string.IsNullOrWhiteSpace(value)) continue;
                string path = MachineEnvironment.Expand(value.Trim().Trim('"'));
                if (File.Exists(path) && seen.Add(path)) list.Add(path);
            }
            return list;
        }

        /// <summary>The LCD library's candidates: its registrations.</summary>
        public static IReadOnlyList<string> LcdCandidates() => Registered(LcdClassId);

        /// <summary>The Arx library's candidates: its registrations.</summary>
        public static IReadOnlyList<string> ArxCandidates() => Registered(ArxClassId);
    }
}
