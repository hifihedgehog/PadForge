using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace PadForge.Services
{
    /// <summary>
    /// One-shot Task Scheduler tasks that give a device back to Windows at the
    /// next boot, for the times no PadForge is left to do it: a crash, a kill,
    /// a power cut or a Windows restart while a keyboard or mouse waits on its
    /// Keep prompt (#549), or while a touchpad is taken (Use Only in PadForge).
    /// Whoever just lost their only keyboard or pointer can still press the
    /// power button, and the login screen that follows must have it back.
    ///
    /// <para>Each task runs as SYSTEM at boot and uses tools Windows ships, and
    /// HidHide's own for a hide, so no PadForge process runs and a moved or
    /// deleted exe changes nothing. It deletes itself once the device is back,
    /// and one whose give-back failed stays for the next boot. A task is
    /// created from an XML definition, the form a boot task of Windows' own
    /// takes (Tasks\Microsoft\Windows\Device Setup\Driver Recovery on Reboot):
    /// the definition states that a laptop on battery runs it, and the action
    /// outgrows the length schtasks' /TR takes (Microsoft, schtasks create).</para>
    ///
    /// <para>schtasks is driven by exit code alone, as
    /// <see cref="PadForge.Common.StartupHelper"/> drives it, since its text is
    /// localized.</para>
    /// </summary>
    internal static class GiveBackTask
    {
        /// <summary>Set by the test assembly for the life of its process, never
        /// by the app. A task made from a test host would run at that PC's next
        /// boot and take a driver or a hide away. While this is set, schtasks
        /// is never started and every call answers as a failed schtasks
        /// does. False in production.</summary>
        internal static bool OffLimitsForTest;

        /// <summary>Test seam: schtasks' arguments to its exit code. Null in
        /// production, where the program runs.</summary>
        internal static Func<string, int> RunSeam;

        /// <summary>Test seam: whether a file is there. Null in production.</summary>
        internal static Func<string, bool> FileExistsSeam;

        /// <summary>How long one schtasks run is waited for.</summary>
        internal const int RunWaitMs = 10000;

        /// <summary>What a touchpad's cover came to.</summary>
        internal enum Outcome
        {
            /// <summary>The task is registered.</summary>
            Registered,
            /// <summary>This PC has no HidHide CLI, as a HidHide PadForge
            /// installed on ARM64 has none: no task can take a hide off.</summary>
            NoTool,
            /// <summary>schtasks refused, or the ID cannot be quoted.</summary>
            Failed,
        }

        private static string System32 => Environment.GetFolderPath(Environment.SpecialFolder.System);
        private static string InfFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");

        /// <summary>The task of a package or a collection, the same in every
        /// session so a later one can delete it. A keyboard's or mouse's goes by
        /// the ID its package binds. The package serves every device of that ID,
        /// so two identical keyboards share one task, and keeping either one
        /// ends it.</summary>
        internal static string NameOf(string id)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes((id ?? string.Empty).ToUpperInvariant()));
            return "PadForge Give Back " + Convert.ToHexString(hash, 0, 8);
        }

        /// <summary>The action for a keyboard or mouse: what
        /// <see cref="VendorUsbDriverInstaller.Restore"/> does, through pnputil.
        /// /delete-driver with /uninstall and /force takes a package off the
        /// devices it serves and out of the driver store (Microsoft, PnPUtil
        /// Command Syntax), where Restore calls DiUninstallDriver and
        /// SetupUninstallOEMInf. The task is made before the package is
        /// stocked, so its published name is not known yet. The published INF
        /// is a copy of the one PadForge wrote, so findstr finds it by the two
        /// lines that say the package is PadForge's and names this ID. /e holds
        /// the ID to the line's end, so an interface's package is not taken for
        /// its device's, and a backslash doubles because findstr reads it as
        /// its escape. A pnputil that fails leaves the task for the next boot,
        /// and so does its 3010 (Microsoft, PnPUtil Return Values), whose
        /// package that boot finds gone. With no package left to remove, the
        /// task deletes itself.</summary>
        internal static string KeyboardCommand(string taskName, string bindId, string infFolder, string system32)
        {
            string findstr = Path.Combine(system32, "findstr.exe");
            string bindLine = "USB_Install, " + bindId.Replace("\\", "\\\\");
            string catalogLine = "CatalogFile = " + VendorUsbDriverInstaller.CatalogName;
            string oemInfs = Path.Combine(infFolder, "oem*.inf");
            return "set \"GIVE_BACK_FAILED=\" "
                + $"& ( for /f \"delims=\" %f in ('{findstr} /m /l /e /c:\"{bindLine}\" \"{oemInfs}\"') do "
                + $"( {findstr} /m /l /c:\"{catalogLine}\" \"%f\" >nul && ( {Path.Combine(system32, "pnputil.exe")} /delete-driver \"%~nxf\" /uninstall /force || set \"GIVE_BACK_FAILED=1\" ) ) ) "
                + "& if not defined GIVE_BACK_FAILED " + SelfDelete(taskName, system32);
        }

        /// <summary>The action for a touchpad: HidHide's CLI takes the hide off
        /// (HidHideCLI Commands.cpp, dev-unhide), then the collection restarts,
        /// so Windows opens it again as TouchpadCapture.RestartForWindows has it
        /// do. /restart-device needs Windows 10 version 2004 (Microsoft, PnPUtil
        /// Command Syntax). On an earlier build the hide still comes off, and a
        /// collection Windows had not opened yet opens as usual. The CLI reads
        /// on until its input ends, so its input is empty. It exits 0 when the
        /// ID is off the list, on it before or not (HidHideCLI.cpp MainApplication,
        /// FilterDriverProxy.cpp BlacklistDelEntry), and with an error when the
        /// control device is missing or another client holds it. An error leaves
        /// the task for the next boot. A CLI that is gone went with its driver,
        /// so no hide is left to take off and the task deletes itself.</summary>
        internal static string TouchpadCommand(string taskName, string instanceId, string cliPath, string system32)
            => $"if exist \"{cliPath}\" "
               + $"( \"{cliPath}\" --dev-unhide \"{instanceId}\" <NUL "
               + $"&& ( {Path.Combine(system32, "pnputil.exe")} /restart-device \"{instanceId}\" & {SelfDelete(taskName, system32)} ) ) "
               + $"else ( {SelfDelete(taskName, system32)} )";

        private static string SelfDelete(string taskName, string system32)
            => $"{Path.Combine(system32, "schtasks.exe")} /Delete /TN \"{taskName}\" /F";

        /// <summary>The task's definition. cmd.exe runs the action, with its
        /// whole line in one pair of quotes (/s).</summary>
        internal static string TaskXml(string command, string system32)
        {
            string program = SecurityElement.Escape(Path.Combine(system32, "cmd.exe"));
            string arguments = SecurityElement.Escape($"/q /d /s /c \"{command}\"");
            return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n"
                + "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n"
                + "  <RegistrationInfo>\r\n"
                + "    <Description>Gives a device back to Windows after PadForge stopped without doing so. It deletes itself once that is done.</Description>\r\n"
                + "  </RegistrationInfo>\r\n"
                + "  <Triggers>\r\n"
                + "    <BootTrigger>\r\n"
                + "      <Enabled>true</Enabled>\r\n"
                + "    </BootTrigger>\r\n"
                + "  </Triggers>\r\n"
                + "  <Principals>\r\n"
                + "    <Principal id=\"LocalSystem\">\r\n"
                + "      <UserId>S-1-5-18</UserId>\r\n"
                + "      <RunLevel>HighestAvailable</RunLevel>\r\n"
                + "    </Principal>\r\n"
                + "  </Principals>\r\n"
                + "  <Settings>\r\n"
                + "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n"
                + "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n"
                + "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n"
                + "    <StartWhenAvailable>true</StartWhenAvailable>\r\n"
                + "    <ExecutionTimeLimit>PT5M</ExecutionTimeLimit>\r\n"
                + "    <Enabled>true</Enabled>\r\n"
                + "  </Settings>\r\n"
                + "  <Actions Context=\"LocalSystem\">\r\n"
                + "    <Exec>\r\n"
                + $"      <Command>{program}</Command>\r\n"
                + $"      <Arguments>{arguments}</Arguments>\r\n"
                + "    </Exec>\r\n"
                + "  </Actions>\r\n"
                + "</Task>\r\n";
        }

        /// <summary>schtasks' arguments that register a task from a definition.
        /// /F replaces one of the name, and /RU names the account the definition
        /// names (Microsoft, schtasks create).</summary>
        internal static string CreateArguments(string taskName, string xmlPath)
            => $"/Create /TN \"{taskName}\" /XML \"{xmlPath}\" /RU SYSTEM /F";

        /// <summary>schtasks' arguments that delete a task.</summary>
        internal static string DeleteArguments(string taskName) => $"/Delete /TN \"{taskName}\" /F";

        /// <summary>Whether an ID can stand in the action's quotes. Windows
        /// allows neither character in the IDs it makes.</summary>
        internal static bool CanQuote(string id)
            => !string.IsNullOrWhiteSpace(id) && id.IndexOf('"') < 0 && id.IndexOf('%') < 0;

        /// <summary>Registers the give-back of a keyboard or mouse before its
        /// package is stocked. False when it could not be, and the bind is then
        /// not made: nobody is asked to risk their only keyboard without it.</summary>
        internal static bool RegisterKeyboard(string bindId, Action<string> log)
        {
            if (!CanQuote(bindId)) return false;
            string name = NameOf(bindId);
            return Register(name, KeyboardCommand(name, bindId, InfFolder, System32), log);
        }

        /// <summary>Registers the give-back of a touchpad before its hide is
        /// written.</summary>
        internal static Outcome RegisterTouchpad(string instanceId, Action<string> log)
        {
            if (!CanQuote(instanceId)) return Outcome.Failed;
            string cli = FindCli(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
            if (cli == null) return Outcome.NoTool;
            string name = NameOf(instanceId);
            return Register(name, TouchpadCommand(name, instanceId, cli, System32), log) ? Outcome.Registered : Outcome.Failed;
        }

        /// <summary>HidHide's CLI where its setup puts it: under x64 by the
        /// setup in use (HidHide Setup/HidHide.aip), at the root by the layout
        /// its repository documents for ARM64 and x64 alike (INSTALL_LAYOUT.md).
        /// Null when neither is there.</summary>
        internal static string FindCli(string programFiles)
        {
            string root = Path.Combine(programFiles, "Nefarius Software Solutions", "HidHide");
            foreach (string path in new[] { Path.Combine(root, "x64", "HidHideCLI.exe"), Path.Combine(root, "HidHideCLI.exe") })
                if ((FileExistsSeam ?? File.Exists)(path)) return path;
            return null;
        }

        private static bool Register(string name, string command, Action<string> log)
        {
            if (RunSeam == null && OffLimitsForTest) return false;
            string xml = Path.Combine(Path.GetTempPath(), "PadForge", "GiveBack", name.Replace(' ', '_') + ".xml");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(xml));
                // The encoding the definition declares, with its byte order mark.
                File.WriteAllText(xml, TaskXml(command, System32), Encoding.Unicode);
                int exit = Run(CreateArguments(name, xml));
                if (exit != 0) log?.Invoke($"schtasks could not register {name} (exit {exit}).");
                return exit == 0;
            }
            catch (Exception ex)
            {
                log?.Invoke($"The task {name} could not be registered: {ex.Message}");
                return false;
            }
            finally
            {
                try { File.Delete(xml); } catch { }
            }
        }

        /// <summary>Deletes the task of a package or collection, whose give-back
        /// is done or no longer wanted. A task that is not there is the same
        /// answer. A task that stays gives the device back at the next boot, the
        /// safe side of a failure.</summary>
        internal static void Delete(string id, Action<string> log = null)
        {
            int exit = Run(DeleteArguments(NameOf(id)));
            log?.Invoke($"Give-back task of {id}: delete exit {exit}.");
        }

        private static int Run(string arguments)
        {
            var seam = RunSeam;
            if (seam != null) return seam(arguments);
            if (OffLimitsForTest) return -1;
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                });
                if (process == null) return -1;
                // Read as it comes, so a full pipe never holds the program.
                process.OutputDataReceived += (_, _) => { };
                process.ErrorDataReceived += (_, _) => { };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                if (process.WaitForExit(RunWaitMs)) return process.ExitCode;
                try { process.Kill(); } catch { }
                return -1;
            }
            catch
            {
                return -1;
            }
        }
    }
}
