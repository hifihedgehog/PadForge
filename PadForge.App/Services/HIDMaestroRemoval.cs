using System;
using System.IO;
using System.Linq;
using HIDMaestro;
using PadForge.Common.Input;
using PadForge.Resources.Strings;

namespace PadForge.Services
{
    /// <summary>
    /// Removes HIDMaestro from the PC from Settings (#516, #518): every virtual
    /// controller and record of one, the driver packages, the signing
    /// certificate, HIDMaestro's registry keys and files, and the shared
    /// Windows keys it changed, put back as they were. HIDMaestro 2.0.1 does
    /// the work in HMUninstall, the same removal its HIDMaestroUninstall.exe
    /// runs, and its docs/UNINSTALL.md names a settings button in a program
    /// such as PadForge as the place for it: Preview to show what would go,
    /// then Run.
    ///
    /// <para>The driver belongs to every program on the PC that embeds
    /// HIDMaestro. A removal refuses while another one runs and names it
    /// (HMUninstallStatus.Blocked). PadForge itself never blocks it: its own
    /// controllers go with everything else. They are still stopped first, so
    /// nothing PadForge holds is left as an object with no device behind it,
    /// and nothing in PadForge creates a controller while the removal runs.
    /// The engine stays stopped afterward, and starting it installs
    /// HIDMaestro again.</para>
    /// </summary>
    internal static class HIDMaestroRemoval
    {
        /// <summary>Test seam for HMUninstall.Preview, null in
        /// production.</summary>
        internal static Func<HMUninstallOptions, HMUninstallReport> PreviewSeam;

        /// <summary>Test seam for the removal itself, null in production,
        /// where <see cref="HMaestroVirtualController.RemoveHIDMaestro"/>
        /// runs it.</summary>
        internal static Func<HMUninstallOptions, HMUninstallReport> RunSeam;

        /// <summary>Test seam for HMContext.MachineBackend, null in
        /// production.</summary>
        internal static Func<HMBackend> MachineBackendSeam;

        /// <summary>Test seam for HMContext.IsUsbipBackendAvailable, null in
        /// production.</summary>
        internal static Func<bool> UsbipAvailableSeam;

        /// <summary>Test seam for the driver store's FileRepository folder,
        /// null in production.</summary>
        internal static Func<string> RepositorySeam;

        /// <summary>
        /// True while the driver of the machine's HIDMaestro backend is
        /// installed. On the USB/IP backend (HMContext.MachineBackend), as
        /// on Windows on ARM, HIDMaestro installs usbip-win2's host
        /// controller in place of its own driver, so the answer is the host
        /// controller's presence (HMContext.IsUsbipBackendAvailable). On the
        /// UMDF2 backend it is HIDMaestro's two driver packages in the driver
        /// store: a <c>hidmaestro.inf_*</c> and a <c>hidmaestro_xusb.inf_*</c>
        /// folder under DriverStore\FileRepository, the folder test of
        /// HIDMaestro's own check (PnputilHelper.IsHidMaestroDriverInstalled
        /// at v2.4.0, whose fast path needs both folders and whose listing
        /// needs every INF of HidMaestroInfNames). One package left by a
        /// removal that could not take the other is no install, and
        /// HIDMaestro installs both again at the next create. HIDMaestro's
        /// check also keeps a positive answer for its process, which this
        /// read, made from outside any HIDMaestro context, does not.
        ///
        /// <para>Never read through an HMContext. Constructing a context
        /// starts HIDMaestro's warm-up, and in an elevated process with no
        /// driver installed the warm-up takes HIDMaestro's owner claim and
        /// stages the driver files under %ProgramFiles%\HIDMaestro, which
        /// after a removal would put them back (HMContext's constructor).
        /// The two probes above are static and construct none.</para>
        /// </summary>
        internal static bool IsDriverInstalled() => IsDriverInstalled(out _);

        /// <summary><see cref="IsDriverInstalled()"/>, with the installed
        /// driver's version: the newest <c>hidmaestro.inf</c> package's
        /// DriverVer, bare, as major.minor.patch
        /// (<see cref="NewestDriverVersion"/>). Null while no driver is
        /// installed, and on the USB/IP backend, where HIDMaestro installs no
        /// driver of its own.</summary>
        internal static bool IsDriverInstalled(out string version)
        {
            version = null;
            try
            {
                HMBackend backend = MachineBackendSeam != null ? MachineBackendSeam() : HMContext.MachineBackend;
                if (backend == HMBackend.Usbip)
                    return UsbipAvailableSeam != null ? UsbipAvailableSeam() : HMContext.IsUsbipBackendAvailable;
                string repository = RepositorySeam != null ? RepositorySeam() : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "DriverStore", "FileRepository");
                if (!Directory.Exists(repository)) return false;
                bool hasXusb = false;
                var main = new System.Collections.Generic.List<string>();
                foreach (string dir in Directory.EnumerateDirectories(repository, "hidmaestro*.inf_*"))
                {
                    string name = Path.GetFileName(dir);
                    if (name.StartsWith("hidmaestro.inf_", StringComparison.OrdinalIgnoreCase)) main.Add(dir);
                    else if (name.StartsWith("hidmaestro_xusb.inf_", StringComparison.OrdinalIgnoreCase)) hasXusb = true;
                }
                if (main.Count == 0 || !hasXusb) return false;
                version = NewestDriverVersion(main);
                return true;
            }
            catch
            {
                version = null;
                return false;
            }
        }

        private static readonly object s_versionGate = new();
        private static string s_versionFolders;
        private static string s_version;

        /// <summary>
        /// The newest DriverVer among the <c>hidmaestro.inf</c> packages in
        /// <paramref name="folders"/>, as major.minor.patch, or null when no
        /// INF there has one. HIDMaestro's own deploy reads the installed
        /// driver the same way (DriverDeploy.InstalledDriver and
        /// InstalledStamps at v2.4.0: the INF in each <c>hidmaestro.inf_*</c>
        /// folder of FileRepository, the newest stamp). HIDMaestro breaks a
        /// tie of one release by the build date, which leaves the release,
        /// the number shown, the same. The INFs are read again only when the
        /// set of folders changes, so the 5 s status timer reads none while
        /// the driver stays.
        /// </summary>
        internal static string NewestDriverVersion(System.Collections.Generic.List<string> folders)
        {
            folders.Sort(StringComparer.OrdinalIgnoreCase);
            string key = string.Join("|", folders);
            lock (s_versionGate)
            {
                if (key == s_versionFolders) return s_version;
            }
            Version newest = null;
            foreach (string dir in folders)
            {
                try
                {
                    string inf = Path.Combine(dir, "hidmaestro.inf");
                    if (!File.Exists(inf)) continue;
                    Version v = Ds3DriverInstaller.ParseInfDriverVersion(File.ReadAllText(inf));
                    if (v != null && (newest == null || v > newest)) newest = v;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            string text = newest == null ? null : newest.Major + "." + newest.Minor + "." + Math.Max(0, newest.Build);
            lock (s_versionGate)
            {
                s_versionFolders = key;
                s_version = text;
            }
            return text;
        }

        /// <summary>What a removal would take and keep, and the programs that
        /// would block it. Changes nothing. Seconds on a PC with many
        /// devices, so callers run it off the UI thread.</summary>
        internal static HMUninstallReport Preview(bool includeUsbip)
        {
            var options = new HMUninstallOptions { IncludeUsbip = includeUsbip };
            return PreviewSeam != null ? PreviewSeam(options) : HMUninstall.Preview(options);
        }

        /// <summary>Stops the engine with <paramref name="stopEngine"/>, which
        /// removes PadForge's virtual controllers and disposes its HIDMaestro
        /// context, then removes HIDMaestro. The removal starts once the stop
        /// returns. A create still running past the stop's wait, which only a
        /// hung HIDMaestro call outlasts, has its context disposed when it
        /// ends, after the removal has begun. Runs on the calling thread to
        /// its end, seconds to minutes, so callers run it on a worker. Null
        /// in the test host, which removes nothing.</summary>
        internal static HMUninstallReport Run(bool includeUsbip, Action stopEngine)
        {
            stopEngine?.Invoke();
            var options = new HMUninstallOptions { IncludeUsbip = includeUsbip };
            return RunSeam != null ? RunSeam(options) : HMaestroVirtualController.RemoveHIDMaestro(options);
        }

        /// <summary>Writes the report to the diagnostics log in English, one
        /// line per item and per program, as HIDMaestro words them.</summary>
        internal static void Log(HMUninstallReport report)
        {
            if (report == null) return;
            PadForge.Engine.SdlDiagLog.WriteLine(
                $"HIDMAESTRO removal {(report.IsPreview ? "preview" : "run")}: {report.Status}, {report.Entries.Count} item(s)");
            foreach (var program in report.RunningPrograms)
                PadForge.Engine.SdlDiagLog.WriteLine($"HIDMAESTRO removal blocked by {program}");
            foreach (var entry in report.Entries)
                PadForge.Engine.SdlDiagLog.WriteLine($"HIDMAESTRO removal {entry}");
        }

        /// <summary>The status line for a finished removal.</summary>
        internal static string StatusLine(HMUninstallReport report) => report.Status switch
        {
            HMUninstallStatus.Removed => Strings.Instance.Status_HIDMaestroRemoved,
            HMUninstallStatus.RemovedRestartPending => Strings.Instance.Status_HIDMaestroRemovedRestart,
            HMUninstallStatus.Blocked => string.Format(Strings.Instance.Status_HIDMaestroRemovalBlocked_Format,
                string.Join(", ", report.RunningPrograms.Select(p => ProgramName(p, fullPath: false)))),
            _ => Strings.Instance.Status_HIDMaestroRemovalFailed,
        };

        /// <summary>How a program that blocks the removal is named: its
        /// executable, or its process ID when Windows would not give the
        /// path, and "?" for a program HIDMaestro knows runs but could not
        /// name at all (HMRunningProgram.ProcessId 0).</summary>
        internal static string ProgramName(HMRunningProgram program, bool fullPath)
        {
            if (!string.IsNullOrEmpty(program.ImagePath))
                return fullPath ? program.ImagePath : Path.GetFileName(program.ImagePath);
            return string.Format(Strings.Instance.HIDMaestroUninstall_Process_Format,
                program.ProcessId > 0 ? program.ProcessId.ToString(System.Globalization.CultureInfo.CurrentCulture) : "?");
        }
    }
}
