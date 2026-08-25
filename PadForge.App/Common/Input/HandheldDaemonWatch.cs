using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Names the vendor daemon, if one runs, that also listens to a
    /// handheld's hidden buttons (issue #343). The daemon keeps emitting
    /// its own keystrokes and launching its own overlay for those buttons
    /// until it is stopped, so the Devices page shows which one is running.
    /// Process names as the vendors ship them (Handheld Companion's watcher
    /// list, plus AYANEO's AYASpace, whose conflict that project records in
    /// a comment). Checked on the sweep cadence, never per poll.
    /// </summary>
    internal static class HandheldDaemonWatch
    {
        // Process image names without .exe.
        private static readonly string[] Names =
        {
            "LegionSpace", "LSDaemon", "LegionGoQuickSettings",        // Lenovo Legion Space
            "ArmouryCrate", "ArmouryCrate.Service", "ArmourySocketServer", "ArmouryCrate.UserSessionHelper", // ASUS Armoury Crate
            "MSI_Center_M_Server", "MSI Center M", "MCMOSDInfo", "MSI Center OSD Info", // MSI Center M
            "ZotacHandheldQuickSetting",                                 // Zotac
            "AYASpace", "AyaSpace",                                      // AYANEO
        };

        private static volatile string _running = string.Empty;

        /// <summary>Comma-joined names of the daemons currently running,
        /// empty when none.</summary>
        public static string Running => _running;

        /// <summary>Re-scans the process list. Worker thread only.</summary>
        public static void Refresh()
        {
            try
            {
                var found = new List<string>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in Process.GetProcesses())
                {
                    string n;
                    try { n = p.ProcessName; } catch { continue; }
                    finally { p.Dispose(); }
                    if (!seen.Add(n)) continue;
                    foreach (var name in Names)
                        if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) { found.Add(n); break; }
                }
                found.Sort(StringComparer.OrdinalIgnoreCase);
                _running = string.Join(", ", found);
            }
            catch { }
        }
    }
}
