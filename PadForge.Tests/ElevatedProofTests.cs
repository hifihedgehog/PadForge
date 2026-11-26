using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Security.Principal;
using System.Threading;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Services;
using PadForge.Services.Automation;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>A test that runs only in a test host with administrator
    /// rights, as PadForge runs. Elsewhere it is skipped and says why.</summary>
    public sealed class ElevatedFactAttribute : FactAttribute
    {
        public ElevatedFactAttribute()
        {
            if (!ElevatedProofTests.TestHostIsElevated())
                Skip = "The test host has no administrator rights. These proofs run in a test host started with them.";
        }
    }

    /// <summary>The lock holder's test: it runs only in the test host the
    /// cross-process lock proof starts, which sets its variables.</summary>
    public sealed class LockHolderFactAttribute : FactAttribute
    {
        public LockHolderFactAttribute()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(ElevatedProofTests.LockNamespaceVariable)))
                Skip = "Run only by ThePrivateNamespaceLock_HeldByAnotherProcessWithRights_IsSeenHeld_AndASecondTakeFindsItHeld, in the test host it starts.";
        }
    }

    /// <summary>
    /// The behaviors the #597 hardening rests on, run with administrator
    /// rights as PadForge has them: a pipe PadForge's own server code makes
    /// is owned by Administrators and passes every client's check under
    /// PadForge's name, the private-namespace lock works, a process with
    /// rights reads as high integrity, the program-input gate holds back
    /// output while a real window of a process with rights is in front or
    /// under the pointer, a program started through the desktop shell
    /// runs at medium integrity, and a program PadForge starts as the user
    /// may not open its own process again by its ID and reads its token
    /// through its own handle. Nothing here starts PadForge, opens its pipes
    /// or touches its windows: every pipe and lock name is the test's own,
    /// and the programs started are cmd.exe writing whoami's lines to a file
    /// of the test's own and the tests' own helper writing its facts to
    /// another.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class ElevatedProofTests
    {
        internal static bool TestHostIsElevated()
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        [ElevatedFact]
        public void APipeTheControlServerMakes_IsOwnedByAdministrators_AndPassesTheChecksUnderPadForgesName()
        {
            string name = "PadForge.Tests.ElevatedControl." + Guid.NewGuid().ToString("N");
            using var service = new ExternalControlService(_ => "ok elevated", name);
            Assert.Equal(ExternalControlService.PipeState.Serving, service.Start());
            // The client run's own exchange takes it and reads its answer.
            Assert.Equal("ok elevated", ControlPipeClient.Exchange(name, "state", 5000));
            // The next instance stands once the server's loop made it.
            Assert.True(SpinWait.SpinUntil(() => ControlPipeClient.Stands(name), 10000));
            using var client = PipeOwner.Open(name);
            client.Connect(5000);
            Assert.Equal(PipeOwner.Administrators, PipeOwner.Of(client.SafePipeHandle));
            // Asked as if it bore PadForge's own name, it passes: no user's
            // own pipe would.
            Assert.True(PipeOwner.IsServer(client.SafePipeHandle, ExternalControlService.PipeName));
            Assert.True(PipeOwner.IsServer(client.SafePipeHandle, AutomationPipe.PipeName));
        }

        [ElevatedFact]
        public void TheAutomationPipe_IsOwnedByAdministrators_AndPassesTheChecksUnderPadForgesName()
        {
            using var rig = new AutomationPipeRig(AutomationLevel.Read);
            using var client = PipeOwner.Open(rig.PipeName);
            client.Connect(5000);
            Assert.Equal(PipeOwner.Administrators, PipeOwner.Of(client.SafePipeHandle));
            Assert.True(PipeOwner.IsServer(client.SafePipeHandle, AutomationPipe.PipeName));
        }

        /// <summary>The variables that make the lock holder below run, set
        /// only by the test that starts it.</summary>
        internal const string LockNamespaceVariable = "PADFORGE_TEST_LOCK_NS", LockReadyVariable = "PADFORGE_TEST_LOCK_READY",
            LockReleaseVariable = "PADFORGE_TEST_LOCK_RELEASE";

        /// <summary>The lock is taken with rights and seen held from another
        /// process with rights, as a second copy of PadForge sees the first:
        /// a test host started by this one, which inherits its rights,
        /// holds it. Windows lets one process open a private namespace
        /// under one alias once (a second open answers ERROR_DUP_NAME), and
        /// every reader of the lock is another process than its holder, so
        /// the proof runs across two.</summary>
        [ElevatedFact]
        public void ThePrivateNamespaceLock_HeldByAnotherProcessWithRights_IsSeenHeld_AndASecondTakeFindsItHeld()
        {
            string ns = "PadForge.Tests.Namespace." + Guid.NewGuid().ToString("N");
            string dir = Path.Combine(Path.GetTempPath(), "PadForge.Tests.Lock." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string ready = Path.Combine(dir, "ready"), release = Path.Combine(dir, "release");
            Assert.False(InstanceLock.IsHeld(ns, "Lock"));
            var psi = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("test");
            psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "PadForge.Tests.dll"));
            psi.ArgumentList.Add("--filter");
            psi.ArgumentList.Add("FullyQualifiedName~ElevatedProofTests.HoldsTheLockForAnotherProcess");
            // A plain environment: the variables this test run's own runner
            // set would make the inner run wait on the outer one.
            var plain = new System.Collections.Generic.Dictionary<string, string>(psi.Environment, StringComparer.OrdinalIgnoreCase);
            psi.Environment.Clear();
            foreach (string keep in new[] { "SystemRoot", "windir", "ComSpec", "PATH", "PATHEXT", "TEMP", "TMP", "USERPROFILE", "USERNAME",
                         "USERDOMAIN", "APPDATA", "LOCALAPPDATA", "ProgramData", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432",
                         "CommonProgramFiles", "CommonProgramFiles(x86)", "SystemDrive", "HOMEDRIVE", "HOMEPATH", "NUMBER_OF_PROCESSORS",
                         "PROCESSOR_ARCHITECTURE", "OS", "COMPUTERNAME" })
                if (plain.TryGetValue(keep, out string value)) psi.Environment[keep] = value;
            psi.Environment[LockNamespaceVariable] = ns;
            psi.Environment[LockReadyVariable] = ready;
            psi.Environment[LockReleaseVariable] = release;
            var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var clock = Stopwatch.StartNew();
            using var holder = new Process { StartInfo = psi };
            holder.OutputDataReceived += (_, e) => { if (e.Data != null) lines.Enqueue(clock.ElapsedMilliseconds + " " + e.Data); };
            holder.ErrorDataReceived += (_, e) => { if (e.Data != null) lines.Enqueue(clock.ElapsedMilliseconds + " err " + e.Data); };
            holder.Start();
            holder.BeginOutputReadLine();
            holder.BeginErrorReadLine();
            string Said() => "after " + clock.ElapsedMilliseconds + " ms, exited=" + holder.HasExited + ", ready=" + File.Exists(ready) + ": " + string.Join(" | ", lines);
            try
            {
                if (!SpinWait.SpinUntil(() => File.Exists(ready) || holder.HasExited, 120000))
                {
                    try { holder.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                    holder.WaitForExit(10000);
                    Assert.Fail("the holder did not take the lock " + Said());
                }
                Assert.True(File.Exists(ready), "the holder ended first " + Said());
                Assert.True(InstanceLock.IsHeld(ns, "Lock"), "not seen held " + Said());
                using (var second = InstanceLock.Take(out bool heldElsewhere, ns, "Lock"))
                {
                    Assert.NotNull(second);
                    Assert.True(heldElsewhere);
                }
            }
            finally
            {
                File.WriteAllText(release, "go");
                Assert.True(holder.WaitForExit(120000), "the holder did not end");
            }
            Assert.Equal(0, holder.ExitCode);
            Assert.Contains(lines, l => l.Contains("Passed!", StringComparison.Ordinal));
            Assert.False(InstanceLock.IsHeld(ns, "Lock"));
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }

        /// <summary>The other process of the test above: takes the lock,
        /// says so, and holds it until told to let go. Runs only when that
        /// test starts it.</summary>
        [LockHolderFact]
        public void HoldsTheLockForAnotherProcess()
        {
            string ns = Environment.GetEnvironmentVariable(LockNamespaceVariable);
            using var taken = InstanceLock.Take(out bool heldBefore, ns, "Lock");
            Assert.NotNull(taken);
            Assert.False(heldBefore);
            File.WriteAllText(Environment.GetEnvironmentVariable(LockReadyVariable), "held");
            Assert.True(SpinWait.SpinUntil(() => File.Exists(Environment.GetEnvironmentVariable(LockReleaseVariable)), 120000));
        }

        [ElevatedFact]
        public void AProcessWithRights_ReadsAsHighIntegrity_AndTheDesktopShellAsMedium()
        {
            Assert.True(ProcessIntegrity.Of((uint)Environment.ProcessId) >= ProcessIntegrity.High);
            IntPtr shell = GetShellWindow();
            Assert.NotEqual(IntPtr.Zero, shell);
            GetWindowThreadProcessId(shell, out uint explorer);
            Assert.Equal(ProcessIntegrity.Medium, ProcessIntegrity.Of(explorer));
        }

        [ElevatedFact]
        public void TheInputGate_HoldsBackOutput_WhileARealWindowWithRightsIsInFrontOrUnderThePointer()
        {
            // A real window of this process, which has rights, made hidden
            // so it takes no place on the desktop and no input goes to it.
            IntPtr mine = CreateWindowExW(0, "STATIC", "PadForge.Tests.Elevated", 0, 0, 0, 10, 10, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            Assert.NotEqual(IntPtr.Zero, mine);
            IntPtr shell = GetShellWindow();
            IntPtr front = mine, under = shell;
            try
            {
                // Only which window is where is stood in. The process each
                // runs in and its integrity are read from Windows.
                ProgramInputGate.InForceSeam = () => true;
                ProgramInputGate.ForegroundSeam = () => front;
                ProgramInputGate.PointerWindowSeam = () => under;
                ProgramInputGate.ForgetForTest();

                Assert.True(ProgramInputGate.HoldsKeys());
                Assert.True(ProgramInputGate.HoldsButtons());

                front = shell;
                under = mine;
                Assert.False(ProgramInputGate.HoldsKeys());
                Assert.True(ProgramInputGate.HoldsButtons());

                // The control: the desktop shell in front and under the
                // pointer holds nothing back.
                under = shell;
                Assert.False(ProgramInputGate.HoldsKeys());
                Assert.False(ProgramInputGate.HoldsButtons());
                Assert.True(ProgramInputGate.IntegrityReads >= 2);
            }
            finally
            {
                ProgramInputGate.InForceSeam = null;
                ProgramInputGate.ForegroundSeam = null;
                ProgramInputGate.PointerWindowSeam = null;
                ProgramInputGate.ForgetForTest();
                DestroyWindow(mine);
            }
        }

        [ElevatedFact]
        public void AProgramStartedThroughTheDesktopShell_RunsAtMediumIntegrity()
        {
            string dir = Path.Combine(Path.GetTempPath(), "PadForge.Tests.ShellStart." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string output = Path.Combine(dir, "groups.txt");
            string cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            string whoami = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "whoami.exe");
            // The route the Run Program macro and every program PadForge
            // opens for the user take once AsTheUser has built its start:
            // ShellLaunch's own start through the desktop. The test host's
            // fence stays up, so the start is made through that member
            // itself.
            var psi = new ProcessStartInfo(cmd, "/d /c \"\"" + whoami + "\" /groups > \"" + output + "\"\"")
            {
                WorkingDirectory = dir,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            var start = new ShellLaunch.UserStart(psi.FileName, ShellLaunch.ArgumentsOf(psi), psi.WorkingDirectory,
                psi.Verb ?? string.Empty, ShellLaunch.ShowOf(psi.WindowStyle));
            var throughTheDesktop = typeof(ShellLaunch).GetMethod("ThroughTheDesktop", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(throughTheDesktop);
            string why = (string)throughTheDesktop.Invoke(null, new object[] { start });
            Assert.True(why == null, why);
            Assert.Contains("why = UserShellSeam != null ? UserShellSeam(start) : ThroughTheDesktop(start);",
                ExtrasPageTests.RepoText("PadForge.App", "Common", "ShellLaunch.cs"));
            try
            {
                string text = null;
                Assert.True(SpinWait.SpinUntil(() =>
                {
                    try { text = File.ReadAllText(output); }
                    catch (IOException) { return false; }
                    return text.Contains("Mandatory Level", StringComparison.Ordinal);
                }, 30000), "whoami wrote nothing");
                Assert.Contains("Medium Mandatory Level", text);
                Assert.DoesNotContain("High Mandatory Level", text);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (IOException) { }
            }
        }

        /// <summary>A program PadForge starts as the user through the desktop
        /// shell (PluginLauncher) may not open its own process again by its
        /// ID: Windows answers access denied. On 2026-11-26 the library check
        /// that runs in such a copy read its rights that way, and every check
        /// the Scripts card started ended on an exception nothing caught. The
        /// same program reads its token through the handle GetCurrentProcess
        /// gives, as PadForge now reads its own rights
        /// (PluginLauncher.ThisProcessHasAdministratorRights), and finds it
        /// not elevated. The refusal is the control: without it the token
        /// read would show nothing. The program is the tests' own helper,
        /// from the test folder, the one folder the launcher's fence lets a
        /// test start a program from.</summary>
        [ElevatedFact]
        public void AProgramStartedAsTheUser_IsRefusedItsOwnProcessByItsID_AndReadsItsTokenThroughItsOwnHandle()
        {
            string probe = PluginLauncherTests.ProbeExe;
            Assert.True(File.Exists(probe), "the helper was not built beside the tests: " + probe);
            string dir = Path.Combine(Path.GetTempPath(), "PadForge.Tests.AsTheUser." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string report = Path.Combine(dir, "report.txt");
            PadForge.Services.Plugins.IPluginProcess process = null;
            try
            {
                var launch = new PadForge.Services.Plugins.PluginLauncher().Start(
                    new PadForge.Services.Plugins.PluginCommand(probe,
                        PadForge.Services.Plugins.PluginFile.Quote(probe) + " rights", Path.GetDirectoryName(probe)),
                    new System.Collections.Generic.Dictionary<string, string> { ["PADFORGE_PROBE_REPORT"] = report });
                Assert.True(launch.Process != null, "not started: " + launch.Refusal + ", error " + launch.Error);
                process = launch.Process;
                Assert.True(SpinWait.SpinUntil(() => File.Exists(report), 20000), "the helper wrote no report");
                var facts = new System.Collections.Generic.Dictionary<string, string>();
                foreach (string line in File.ReadAllLines(report))
                {
                    int equals = line.IndexOf('=');
                    if (equals > 0) facts[line.Substring(0, equals)] = line.Substring(equals + 1);
                }
                Assert.Equal("0", facts["privileged"]);
                // 5 is access denied.
                Assert.Equal("5", facts["selfopen"]);
                Assert.Equal("0", facts["owntoken"]);
            }
            finally
            {
                process?.Stop();
                process?.Dispose();
                try { Directory.Delete(dir, true); } catch (IOException) { }
            }
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style,
            int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyWindow(IntPtr window);
    }
}
