using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Names Flydigi's Space Station processes when they run (discussion
    /// #395). A Vader 5 Pro whose owner had whitelisted
    /// <c>SpaceStationService.exe</c> in HidHide re-enumerated at the USB
    /// level every few seconds to every few minutes while PadForge ran, and
    /// stopped the moment the service was stopped or lost sight of the pad.
    /// SDL's Flydigi driver and the service both drive the controller's
    /// vendor interface (Get Info, Get Status and the Acquire heartbeat on
    /// SDL's side), and the service is the party PadForge cannot see, so
    /// the Devices page names it while it runs and the arrival log records
    /// whether it could reach the pad. Modeled on
    /// <see cref="HandheldDaemonWatch"/>: process names as Flydigi ships
    /// them, refreshed on the device sweep, never per poll.
    /// </summary>
    internal static class FlydigiServiceWatch
    {
        // Process image names without .exe, as installed by
        // FlydigiSpaceStation_setup (C:\Program Files\Flydigi Space Station\).
        private static readonly string[] Names =
        {
            "SpaceStationService",   // the background service, as the user's 4.x-era install names it
            "GameControllerService", // the same service in the 3.4.x installer (Service\GameControllerService.exe)
            "Flydigi Space Station", // the desktop app
        };

        private static volatile string _running = string.Empty;
        private static volatile string _detail = "none";

        /// <summary>Comma-joined image names of the Flydigi processes
        /// currently running, empty when none.</summary>
        public static string Running => _running;

        /// <summary>Log detail: each running process with its pid, file
        /// version and path, or "none". Built once per refresh so the arrival
        /// log never walks the process list itself.</summary>
        public static string Detail => _detail;

        /// <summary>Flydigi controllers as SDL knows them
        /// (SDL_IsJoystickFlydigiController): every 37D7 product, plus the
        /// first-generation 04B4:2412 gamepad on a Cypress vendor id.</summary>
        public static bool IsFlydigiDevice(ushort vendorId, ushort productId)
            => vendorId == 0x37D7 || (vendorId == 0x04B4 && productId == 0x2412);

        /// <summary>Re-scans the process list. Worker thread only. Returns
        /// whether <see cref="Running"/> changed, so the device sweep can
        /// republish rows when the service starts or stops between arrivals.
        /// Every matching pid lands in <see cref="Detail"/>. A failed scan
        /// keeps the last names and says so in the detail.</summary>
        public static bool Refresh()
        {
            string before = _running;
            Process[] procs = null;
            try
            {
                var found = new List<string>();
                var details = new List<string>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                procs = Process.GetProcesses();
                foreach (var p in procs)
                {
                    string n;
                    try { n = p.ProcessName; }
                    catch { continue; }
                    bool match = false;
                    foreach (var name in Names)
                        if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) { match = true; break; }
                    if (!match) continue;
                    if (seen.Add(n)) found.Add(n);
                    details.Add(Describe(p, n));
                }
                found.Sort(StringComparer.OrdinalIgnoreCase);
                _running = string.Join(", ", found);
                _detail = details.Count == 0 ? "none" : string.Join(" | ", details);
            }
            catch (Exception ex)
            {
                _detail = "scan-failed " + ex.GetType().Name;
            }
            finally
            {
                if (procs != null)
                    foreach (var p in procs) { try { p.Dispose(); } catch { } }
            }
            return !string.Equals(before, _running, StringComparison.Ordinal);
        }

        /// <summary>The one FLYDIGI arrival record, built here so a test reads
        /// the emitted line rather than the production source. The HidHide
        /// snapshot is a configuration read, not proof of an open handle.</summary>
        public static string DescribeArrival(ushort vendorId, ushort productId, uint sdlInstanceId,
            string backend, string devicePath, string hidHideSnapshot)
            => $"FLYDIGI arrival {vendorId:X4}:{productId:X4} sdl={sdlInstanceId} backend={backend} path={devicePath} service=[{Detail}] {hidHideSnapshot}";

        private static string Describe(Process p, string name)
        {
            // MainModule needs access the caller may not have for a SYSTEM
            // service even when elevated. The name and pid always print.
            string path = "", version = "";
            try
            {
                var mm = p.MainModule;
                path = mm?.FileName ?? "";
                version = mm?.FileVersionInfo?.FileVersion ?? "";
            }
            catch { }
            return path.Length > 0
                ? $"{name}.exe pid={p.Id} v={version} path={path}"
                : $"{name}.exe pid={p.Id}";
        }
    }
}
