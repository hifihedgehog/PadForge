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

        /// <summary>
        /// True while the driver of the machine's HIDMaestro backend is
        /// installed, the answer HIDMaestro's own
        /// HMContext.IsDriverInstalled gives for Auto. On the USB/IP backend
        /// (HMContext.MachineBackend), as on Windows on ARM, HIDMaestro
        /// installs usbip-win2's host controller in place of its own driver,
        /// so the answer is the host controller's presence
        /// (HMContext.IsUsbipBackendAvailable). On the UMDF2 backend it is
        /// HIDMaestro's driver package in the driver store: a
        /// <c>hidmaestro.inf_*</c> folder under DriverStore\FileRepository,
        /// the first test HIDMaestro's own check makes
        /// (PnputilHelper.IsHidMaestroDriverInstalled).
        ///
        /// <para>Never read through an HMContext. Constructing a context
        /// starts HIDMaestro's warm-up, and in an elevated process with no
        /// driver installed the warm-up takes HIDMaestro's owner claim and
        /// stages the driver files under %ProgramFiles%\HIDMaestro, which
        /// after a removal would put them back (HMContext's constructor).
        /// The two probes above are static and construct none.</para>
        /// </summary>
        internal static bool IsDriverInstalled()
        {
            try
            {
                HMBackend backend = MachineBackendSeam != null ? MachineBackendSeam() : HMContext.MachineBackend;
                if (backend == HMBackend.Usbip)
                    return UsbipAvailableSeam != null ? UsbipAvailableSeam() : HMContext.IsUsbipBackendAvailable;
                string repository = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "DriverStore", "FileRepository");
                return Directory.Exists(repository)
                    && Directory.EnumerateDirectories(repository, "hidmaestro.inf_*").Any();
            }
            catch
            {
                return false;
            }
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
        /// context, then removes HIDMaestro. Runs on the calling thread to
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
