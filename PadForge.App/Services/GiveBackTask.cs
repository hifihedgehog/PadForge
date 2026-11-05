using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace PadForge.Services
{
    /// <summary>
    /// One-shot Task Scheduler tasks that give a device back to Windows when no
    /// PadForge is left to do it: a crash, a kill, a power cut or a Windows
    /// restart while a keyboard or mouse waits on its Keep prompt (#549), or
    /// while a touchpad is taken (Use Only in PadForge). Whoever just lost their
    /// only keyboard or pointer can still press the power button, and the login
    /// screen that follows must have it back.
    ///
    /// <para>Each task runs as SYSTEM, as soon as it is registered and at boot.
    /// The app holds a pipe for it and writes nothing, and the task's first step
    /// reads that pipe with find.exe. Windows closes a pipe's server with its
    /// process however that ends, so the read ends at once when the app is
    /// killed, and costs a cmd.exe and a find.exe until then. It ends too when
    /// the app deletes the task, and at a boot no pipe is there to read. If the
    /// task still stands after that, a device is owed: PadForge.exe starts in
    /// <see cref="GiveBackRun"/>'s mode, the same on every platform. Instances run
    /// in parallel (Microsoft, MultipleInstancesPolicy), so a registration always
    /// starts a wait of its own, whatever an earlier one is doing. An exe moved
    /// or deleted since, or a run that did not get through, falls back to tools
    /// Windows ships, and HidHide's own for a hide. Either way the task goes once
    /// the device is back, and one whose give-back failed stays for the next
    /// boot. A task is
    /// created from an XML definition, the form a boot task of Windows' own
    /// takes (Tasks\Microsoft\Windows\Device Setup\Driver Recovery on Reboot):
    /// the definition states that a laptop on battery runs it and that a run may
    /// wait for as long as the app runs (Microsoft, ExecutionTimeLimit: PT0S), and
    /// the action outgrows the length schtasks' /TR takes (Microsoft, schtasks
    /// create).</para>
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

        /// <summary>Test seam: PadForge's own exe. Null in production.</summary>
        internal static Func<string> ExePathSeam;

        /// <summary>Test seam: the wait for a run to finish. Null in production.</summary>
        internal static Action<string> RunFinishSeam;

        /// <summary>Test seam: an exe's size, negative when it cannot be read. Null in
        /// production.</summary>
        internal static Func<string, long> ExeSizeSeam;

        /// <summary>Test seam: where a single-file exe's native libraries are extracted.
        /// Null in production.</summary>
        internal static Func<string> ExtractBaseSeam;

        /// <summary>The pipes this app holds, by task. A registration holds one
        /// for the wait it starts. They go when the task is deleted, and with the
        /// process however it ends.</summary>
        private static readonly Dictionary<string, List<NamedPipeServerStream>> s_pipes = new(StringComparer.OrdinalIgnoreCase);

        private static string OwnExe => ExePathSeam != null ? ExePathSeam() : Environment.ProcessPath;

        /// <summary>How long one schtasks run is waited for.</summary>
        internal const int RunWaitMs = 10000;

        /// <summary>What a touchpad's cover came to.</summary>
        internal enum Outcome
        {
            /// <summary>The task is registered.</summary>
            Registered,
            /// <summary>PadForge's own path cannot stand in the action's quotes
            /// and this PC has no HidHide CLI, so no task can take a hide off.</summary>
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

        /// <summary>The name of the pipe a task's wait reads.</summary>
        internal static string PipeName(string taskName) => taskName.Replace(' ', '_');

        private static string PipePath(string taskName) => @"\\.\pipe\" + PipeName(taskName);

        /// <summary>The name of the pipe a run holds while it gives a device back,
        /// which makes it the only run of its task. Pipes, since a run and the app
        /// are in two sessions and two accounts, and a name is one for all of
        /// them.</summary>
        internal static string RunPipeName(string taskName) => PipeName(taskName) + "_run";

        /// <summary>How long a registration waits for a run giving the device back
        /// to finish, so it does not take a hide or a package off after this app
        /// wrote its own. An attempt takes a moment, and past this one a run that
        /// sees the app's pipe leaves what it has not done.</summary>
        internal const int RunFinishWaitMs = 3000;

        /// <summary>The size of the exe registering a task. A task left through two
        /// boots can outlive that exe, and an older one has no run mode: it would start
        /// the whole app as SYSTEM. So the run goes only to a file of this size, which
        /// cmd reads with no help (%~z). Any other file falls to what stands behind the
        /// run, and its next start registers the task for itself.</summary>
        private static long ExeSize(string exe)
        {
            if (ExeSizeSeam != null) return ExeSizeSeam(exe);
            try { return new FileInfo(exe).Length; }
            catch { return -1; }
        }

        /// <summary>Where this process's native libraries are extracted, the default
        /// the host itself uses (Microsoft, single-file deployment). A run starts from
        /// the same place, so SYSTEM's profile takes no copy of its own, and the run is
        /// up at once.</summary>
        private static string ExtractBase()
        {
            if (ExtractBaseSeam != null) return ExtractBaseSeam();
            return Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR") is { Length: > 0 } set
                ? set
                : Path.Combine(Path.GetTempPath(), ".net");
        }

        /// <summary>A task's whole action. It waits while the app holds the pipe,
        /// then gives the device back only if the task still stands: a device
        /// kept or given back has no task, and a normal exit starts nothing. A
        /// missing or other exe, or a run that exits 1, falls to
        /// <paramref name="otherwise"/>, when there is one. The reader is
        /// find.exe, since cmd's own type ends at once on a pipe.</summary>
        internal static string Action(string taskName, string exe, string runSwitch, string id, string otherwise, string system32)
        {
            string run = null;
            if (CanQuote(exe))
            {
                string extract = ExtractBase();
                run = (CanQuote(extract) ? $"set \"DOTNET_BUNDLE_EXTRACT_BASE_DIR={extract}\" & " : string.Empty)
                      + $"\"{exe}\" {runSwitch} \"{id}\" \"{taskName}\"";
            }
            string giveBack = run == null ? otherwise : otherwise == null ? run : $"{run} || ( {otherwise} )";
            long size = run == null ? -1 : ExeSize(exe);
            if (size >= 0)
                giveBack = $"for %I in (\"{exe}\") do if \"%~zI\"==\"{size}\" ( {giveBack} )"
                           + (otherwise == null ? string.Empty : $" else ( {otherwise} )");
            return $"{Path.Combine(system32, "find.exe")} /v \"\" < \"{PipePath(taskName)}\" >nul 2>&1 "
                + $"& {Path.Combine(system32, "schtasks.exe")} /Query /TN \"{taskName}\" >nul 2>&1 && ( {giveBack} )";
        }

        /// <summary>The fallback for a keyboard or mouse: what
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

        /// <summary>The fallback for a touchpad: HidHide's CLI takes the hide off
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
        /// whole line in one pair of quotes (/s). A registration trigger starts
        /// the wait now (Microsoft, registrationTrigger), and only when the app
        /// holds the pipe for it, since with no pipe the wait ends at once. A boot
        /// trigger stands either way. The wait lasts as long as the app, so a
        /// registration's instance has no time limit (Microsoft, ExecutionTimeLimit:
        /// PT0S), and a boot's has no wait to last, so it has one, and nothing is left
        /// running past it (Microsoft, ExecutionTimeLimit of a trigger).</summary>
        internal static string TaskXml(string command, string system32, bool pipeHeld)
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
                + "      <ExecutionTimeLimit>PT10M</ExecutionTimeLimit>\r\n"
                + "    </BootTrigger>\r\n"
                + (pipeHeld ? "    <RegistrationTrigger>\r\n      <Enabled>true</Enabled>\r\n    </RegistrationTrigger>\r\n" : string.Empty)
                + "  </Triggers>\r\n"
                + "  <Principals>\r\n"
                + "    <Principal id=\"LocalSystem\">\r\n"
                + "      <UserId>S-1-5-18</UserId>\r\n"
                + "      <RunLevel>HighestAvailable</RunLevel>\r\n"
                + "    </Principal>\r\n"
                + "  </Principals>\r\n"
                + "  <Settings>\r\n"
                + "    <MultipleInstancesPolicy>Parallel</MultipleInstancesPolicy>\r\n"
                + "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n"
                + "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n"
                + "    <StartWhenAvailable>true</StartWhenAvailable>\r\n"
                + "    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\r\n"
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
            return Register(name, Action(name, OwnExe, GiveBackRun.PackageSwitch, bindId,
                KeyboardCommand(name, bindId, InfFolder, System32), System32), log);
        }

        /// <summary>Registers the give-back of a touchpad before its hide is
        /// written.</summary>
        internal static Outcome RegisterTouchpad(string instanceId, Action<string> log)
        {
            if (!CanQuote(instanceId)) return Outcome.Failed;
            string cli = FindCli(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
            string exe = OwnExe;
            if (cli == null && !CanQuote(exe)) return Outcome.NoTool;
            string name = NameOf(instanceId);
            string otherwise = cli != null ? TouchpadCommand(name, instanceId, cli, System32) : null;
            return Register(name, Action(name, exe, GiveBackRun.TouchpadSwitch, instanceId, otherwise, System32), log)
                ? Outcome.Registered : Outcome.Failed;
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
            // The pipe stands before the task, so the wait its registration
            // starts finds it.
            var pipe = HoldPipe(name);
            bool registered = false;
            try
            {
                // Its pipe stands first, so a run that took its turn after this
                // look sees the pipe and leaves the device to this app.
                WaitForARunToFinish(name);
                Directory.CreateDirectory(Path.GetDirectoryName(xml));
                // The encoding the definition declares, with its byte order mark.
                File.WriteAllText(xml, TaskXml(command, System32, pipe != null), Encoding.Unicode);
                int exit = Run(CreateArguments(name, xml));
                if (exit != 0) log?.Invoke($"schtasks could not register {name} (exit {exit}).");
                return registered = exit == 0;
            }
            catch (Exception ex)
            {
                log?.Invoke($"The task {name} could not be registered: {ex.Message}");
                return false;
            }
            finally
            {
                if (!registered) LetGoOfOne(name, pipe);
                try { File.Delete(xml); } catch { }
            }
        }

        /// <summary>Holds one more pipe for a task. An instance serves one reader,
        /// so a later registration of a task gets its own. A reader can connect
        /// before the server calls ConnectNamedPipe (Microsoft, ConnectNamedPipe),
        /// so this never calls it. Null when Windows refused the pipe.</summary>
        private static NamedPipeServerStream HoldPipe(string name)
        {
            try
            {
                var pipe = new NamedPipeServerStream(PipeName(name), PipeDirection.Out, NamedPipeServerStream.MaxAllowedServerInstances);
                lock (s_pipes)
                {
                    if (!s_pipes.TryGetValue(name, out var held)) s_pipes[name] = held = new List<NamedPipeServerStream>();
                    held.Add(pipe);
                }
                return pipe;
            }
            catch
            {
                return null;
            }
        }

        private static void WaitForARunToFinish(string name)
        {
            var seam = RunFinishSeam;
            if (seam != null)
            {
                seam(name);
                return;
            }
            long until = Environment.TickCount64 + RunFinishWaitMs;
            while (AnyInstance(RunPipeName(name)) && Environment.TickCount64 < until)
                System.Threading.Thread.Sleep(50);
        }

        /// <summary>Closes every pipe of a task, which ends the waits that read
        /// them.</summary>
        private static void LetGo(string name)
        {
            lock (s_pipes)
            {
                if (!s_pipes.Remove(name, out var held)) return;
                foreach (var pipe in held)
                {
                    try { pipe.Dispose(); } catch { }
                }
            }
        }

        /// <summary>Closes the pipe of a registration that failed. One from before
        /// it stays, with the task it reads for.</summary>
        private static void LetGoOfOne(string name, NamedPipeServerStream pipe)
        {
            if (pipe == null) return;
            lock (s_pipes)
            {
                if (s_pipes.TryGetValue(name, out var held) && held.Remove(pipe) && held.Count == 0) s_pipes.Remove(name);
            }
            try { pipe.Dispose(); } catch { }
        }

        /// <summary>Deletes the task of a package or collection, whose give-back
        /// is done or no longer wanted. A task that is not there is the same
        /// answer. A task that stays keeps its pipe, and gives the device back
        /// when the app ends or at the next boot, the safe side of a
        /// failure.</summary>
        internal static void Delete(string id, Action<string> log = null)
        {
            string name = NameOf(id);
            int exit = Run(DeleteArguments(name));
            log?.Invoke($"Give-back task of {id}: delete exit {exit}.");
            if (exit == 0 || !Stands(name)) LetGo(name);
        }

        /// <summary>Whether a task of this name is registered. A device kept or
        /// given back has no task. Only schtasks saying no says it is gone, 1 for a
        /// task it does not have (measured). A schtasks that did not answer says
        /// nothing, and a device that may be owed stays owed.</summary>
        internal static bool Stands(string taskName) => Run($"/Query /TN \"{taskName}\"") != 1;

        /// <summary>Whether an app holds a pipe for this task now, with a reader
        /// on it or not.</summary>
        internal static bool AnAppHolds(string taskName) => AnyInstance(PipeName(taskName));

        /// <summary>Whether a pipe of this name has an instance, free or busy
        /// (Microsoft, WaitNamedPipe: with none it returns at once).</summary>
        private static bool AnyInstance(string pipeName)
        {
            const int ERROR_FILE_NOT_FOUND = 2;
            return WaitNamedPipe(@"\\.\pipe\" + pipeName, 1) || Marshal.GetLastPInvokeError() != ERROR_FILE_NOT_FOUND;
        }

        [DllImport("kernel32.dll", EntryPoint = "WaitNamedPipeW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WaitNamedPipe(string name, uint timeoutMs);

        /// <summary>Deletes a task by its name, a run's last act.</summary>
        internal static void DeleteByName(string taskName) => Run(DeleteArguments(taskName));

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
