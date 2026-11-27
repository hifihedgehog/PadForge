using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using PadForge.Common;
using PadForge.Services.Automation;

namespace PadForge.Services.Plugins
{
    /// <summary>Why a plugin was not started. Every one but
    /// <see cref="None"/> leaves the plugin stopped with its row saying
    /// why.</summary>
    internal enum PluginRefusal
    {
        None,
        /// <summary>The file is not where it was approved.</summary>
        Missing,
        /// <summary>No command opens this kind of file for the desktop's
        /// user.</summary>
        NoProgram,
        /// <summary>No command opens Python files for the desktop's user,
        /// and no py launcher is in the places it installs to.</summary>
        NoPython,
        /// <summary>The command that opens this kind of file for the
        /// desktop's user names a program that is not there.</summary>
        ProgramMissing,
        /// <summary>The desktop's user's part of the registry could not be
        /// read for the command that opens this kind of file.</summary>
        ProgramUnreadable,
        /// <summary>The program asks for administrator rights in its
        /// manifest (error 740).</summary>
        NeedsAdministrator,
        /// <summary>Windows refused the start, or refused to read the
        /// variables Windows builds for the shell's user, with
        /// <see cref="PluginLaunch.Error"/>.</summary>
        Failed,
        /// <summary>The test host does not start this program.</summary>
        OffLimits,
    }

    /// <summary>What a plugin is started as: the program, its whole
    /// command line and the folder it starts in. One with no program says
    /// why in <see cref="Refusal"/>.</summary>
    internal sealed record PluginCommand(string Program, string CommandLine, string Folder)
    {
        public PluginRefusal Refusal { get; init; }

        /// <summary>No program, for <paramref name="why"/>.</summary>
        internal static PluginCommand Without(PluginRefusal why) => new(null, null, null) { Refusal = why };
    }

    /// <summary>A start's outcome: the process, or why there is none.</summary>
    internal sealed class PluginLaunch
    {
        public IPluginProcess Process { get; init; }
        public PluginRefusal Refusal { get; init; }

        /// <summary>The Windows error of a <see cref="PluginRefusal.Failed"/>
        /// start.</summary>
        public int Error { get; init; }

        public static PluginLaunch Refused(PluginRefusal why, int error = 0) => new() { Refusal = why, Error = error };
    }

    /// <summary>A plugin's process as PadForge holds it.</summary>
    internal interface IPluginProcess : IDisposable
    {
        /// <summary>The process PadForge started.</summary>
        int Id { get; }

        /// <summary>Whether a pipe's client with this process ID is the
        /// process PadForge started, or one it started in turn, as a script
        /// launcher starts the interpreter. Read while the handle is held,
        /// so a reused ID is never taken for it.</summary>
        bool Owns(int processId);

        /// <summary>Raised once, on a worker, with the exit code, when the
        /// process ends by itself or by <see cref="Stop"/>.</summary>
        event Action<int> Exited;

        /// <summary>Ends the process and everything it started, by closing
        /// the job it runs in.</summary>
        void Stop();

        /// <summary>Whether a debugger is attached to the process
        /// (CheckRemoteDebuggerPresent, #600 item 18), so a plugin stopped
        /// at a breakpoint is not ended for its silence.</summary>
        bool DebuggerAttached => false;
    }

    /// <summary>Starts a plugin's process. The app's is
    /// <see cref="PluginLauncher"/>. A test gives its own.</summary>
    internal interface IPluginLauncher
    {
        PluginLaunch Start(PluginCommand command, IReadOnlyDictionary<string, string> variables);
    }

    /// <summary>
    /// Starts a plugin (#600) as a child of PadForge, with PadForge's rights
    /// and a copy of its variables, the given variables in place of any of
    /// the same name (the one-time key and the pipe's name) and no
    /// <c>__COMPAT_LAYER</c> (<see cref="UserVariables"/>).
    ///
    /// <para>The plugin is made suspended, put in a job object of its own
    /// that ends every process in it when the job's last handle closes, and
    /// only then let run. PadForge holds that handle, so the plugin and
    /// whatever it starts end when PadForge stops it, closes or dies.</para>
    /// </summary>
    internal sealed class PluginLauncher : IPluginLauncher
    {
        /// <summary>Set for the test host's whole life: only a program in the
        /// test host's own folder, the helper the tests build, is
        /// started.</summary>
        internal static bool OffLimitsForTest;

        /// <summary>Whether a program may be started where the test host's
        /// fence stands: one in the folder the test host runs from.</summary>
        private static bool Refuses(string program)
        {
            if (!OffLimitsForTest) return false;
            if (string.IsNullOrEmpty(program)) return true;
            string folder = Path.GetFullPath(AppContext.BaseDirectory);
            string full = Path.GetFullPath(program);
            return !full.StartsWith(folder, StringComparison.OrdinalIgnoreCase);
        }

        public PluginLaunch Start(PluginCommand command, IReadOnlyDictionary<string, string> variables)
        {
            if (Refuses(command?.Program)) return PluginLaunch.Refused(PluginRefusal.OffLimits);
            if (command?.Program == null) return PluginLaunch.Refused(command?.Refusal is { } why and not PluginRefusal.None ? why : PluginRefusal.NoProgram);

            IntPtr environment = Marshal.StringToHGlobalUni(UserVariables.Block(OwnVariables(), variables));
            try
            {
                var startup = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
                var line = new StringBuilder(command.CommandLine);
                uint flags = CreateUnicodeEnvironment | CreateNoWindow | CreateSuspended;
                if (!CreateProcessW(command.Program, line, IntPtr.Zero, IntPtr.Zero, false, flags, environment,
                        command.Folder, ref startup, out var info))
                {
                    int error = Marshal.GetLastWin32Error();
                    return PluginLaunch.Refused(error == ErrorElevationRequired ? PluginRefusal.NeedsAdministrator : PluginRefusal.Failed, error);
                }

                var process = new SafeProcessHandle(info.hProcess, true);
                IntPtr thread = info.hThread;
                var job = CreateJobObjectW(IntPtr.Zero, null);
                if (job.IsInvalid || !KillOnClose(job) || !AssignProcessToJobObject(job, process))
                {
                    int code = Marshal.GetLastWin32Error();
                    TerminateProcess(process, 1);
                    CloseHandle(thread);
                    process.Dispose();
                    job.Dispose();
                    return PluginLaunch.Refused(PluginRefusal.Failed, code);
                }

                ResumeThread(thread);
                CloseHandle(thread);
                return new PluginLaunch { Process = new PluginProcess(info.dwProcessId, process, job) };
            }
            finally
            {
                Marshal.FreeHGlobal(environment);
            }
        }

        /// <summary>This process's variables, one line each.</summary>
        private static string[] OwnVariables()
        {
            var lines = new List<string>();
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
                lines.Add(entry.Key + "=" + entry.Value);
            return lines.ToArray();
        }

        /// <summary>The desktop shell's process, opened to be a new process's
        /// parent and to read its token, or null when no shell runs.</summary>
        internal static SafeProcessHandle OpenShell()
        {
            IntPtr shell = GetShellWindow();
            if (shell == IntPtr.Zero) return null;
            GetWindowThreadProcessId(shell, out uint shellId);
            if (shellId == 0) return null;
            var process = OpenProcess(ProcessCreateProcess | ProcessQueryLimitedInformation, false, shellId);
            if (!process.IsInvalid) return process;
            process.Dispose();
            return null;
        }

        /// <summary>Whether a process's token has administrator rights: it is
        /// elevated, or runs at high integrity or above, as every token of an
        /// administrator does with UAC off. Null when its token cannot be
        /// read, which a caller takes as yes.</summary>
        internal static bool? HasAdministratorRights(SafeProcessHandle process)
            => OpenProcessToken(process, TokenQuery, out var token) ? RightsOf(token) : null;

        /// <summary>Whether this process's own token has administrator
        /// rights, read as <see cref="HasAdministratorRights"/> reads any
        /// process's, through the handle GetCurrentProcess gives. That
        /// handle needs no rights on the process
        /// object. Opening this process again by its ID does: a copy PadForge
        /// starts as the user (<see cref="Start"/>) is refused its own
        /// process, and the install check's read of its rights through
        /// Process.SafeHandle failed there with access denied on
        /// 2026-11-26.</summary>
        internal static bool? ThisProcessHasAdministratorRights()
            => OpenProcessToken(GetCurrentProcess(), TokenQuery, out var token) ? RightsOf(token) : null;

        private static bool? RightsOf(SafeAccessTokenHandle token)
        {
            using (token)
            {
                int? elevated = AutomationClientIdentity.ReadInt(token, TokenElevation);
                int? integrity = AutomationClientIdentity.ReadIntegrity(token);
                if (elevated == null || integrity == null) return null;
                return elevated.Value != 0 || integrity.Value >= HighIntegrity;
            }
        }

        private static bool KillOnClose(SafeFileHandle job)
        {
            var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            limits.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
            return SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref limits,
                Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>());
        }

        /// <summary>A started plugin: its handle, held so its process ID is
        /// never reused while PadForge watches it, and its job.</summary>
        private sealed class PluginProcess : IPluginProcess
        {
            private readonly SafeProcessHandle _process;
            private readonly object _gate = new();
            private SafeFileHandle _job;
            private RegisteredWaitHandle _wait;
            private readonly ProcessWait _handle;
            private int _exited;

            public PluginProcess(int id, SafeProcessHandle process, SafeFileHandle job)
            {
                Id = id;
                _process = process;
                _job = job;
                _handle = new ProcessWait(process);
                _wait = ThreadPool.RegisterWaitForSingleObject(_handle, (_, _) => OnExit(), null, Timeout.Infinite, executeOnlyOnce: true);
            }

            public int Id { get; }

            private Action<int> _exitedHandlers;
            private int? _code;

            public event Action<int> Exited
            {
                add
                {
                    int? code;
                    lock (_gate)
                    {
                        code = _code;
                        if (code == null) _exitedHandlers += value;
                    }
                    // One that ended before anyone listened is told at once.
                    if (code != null) value?.Invoke(code.Value);
                }
                remove { lock (_gate) _exitedHandlers -= value; }
            }

            public bool DebuggerAttached
            {
                get
                {
                    try { return !_process.IsClosed && CheckRemoteDebuggerPresent(_process, out bool present) && present; }
                    catch (ObjectDisposedException) { return false; }
                }
            }

            public bool Owns(int processId)
            {
                if (processId <= 0) return false;
                if (processId == Id) return true;
                SafeFileHandle job;
                lock (_gate) job = _job;
                if (job == null || job.IsClosed) return false;
                using var other = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
                if (other.IsInvalid) return false;
                return IsProcessInJob(other, job, out bool inJob) && inJob;
            }

            // The process whose exit this thread is telling, so a Dispose
            // made from inside an Exited handler does not wait for itself.
            [ThreadStatic] private static PluginProcess t_telling;

            private void OnExit()
            {
                if (Interlocked.Exchange(ref _exited, 1) != 0) return;
                t_telling = this;
                try { Tell(); }
                finally { t_telling = null; }
            }

            private void Tell()
            {
                int code = GetExitCodeProcess(_process, out uint exit) ? unchecked((int)exit) : -1;
                Action<int> handlers;
                lock (_gate)
                {
                    _code = code;
                    handlers = _exitedHandlers;
                    _exitedHandlers = null;
                }
                // Whatever the plugin started goes with it.
                CloseJob();
                handlers?.Invoke(code);
            }

            private void CloseJob()
            {
                SafeFileHandle job;
                lock (_gate)
                {
                    job = _job;
                    _job = null;
                }
                job?.Dispose();
            }

            public void Stop() => CloseJob();

            public void Dispose()
            {
                CloseJob();
                var wait = Interlocked.Exchange(ref _wait, null);
                if (wait != null)
                {
                    // The handle stays open until the wait no longer reads it.
                    if (t_telling == this) wait.Unregister(null);
                    else
                    {
                        using var done = new ManualResetEvent(false);
                        if (wait.Unregister(done)) done.WaitOne(2000);
                    }
                }
                _handle.Dispose();
                _process.Dispose();
            }
        }

        private sealed class ProcessWait : WaitHandle
        {
            public ProcessWait(SafeProcessHandle process)
                => SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false);
        }

        // ── Windows ──

        private const uint ProcessCreateProcess = 0x0080;
        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const uint TokenQuery = 0x0008;
        private const int TokenElevation = 20;
        private const int TokenElevationTypeClass = 18;
        private const int HighIntegrity = 0x3000;
        private const uint CreateUnicodeEnvironment = 0x00000400;
        private const uint CreateNoWindow = 0x08000000;
        private const uint CreateSuspended = 0x00000004;
        private const int ErrorElevationRequired = 740;
        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JobObjectLimitKillOnJobClose = 0x2000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }


        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CheckRemoteDebuggerPresent(SafeProcessHandle process, out bool present);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint processId);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();




        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern bool CreateProcessW(string application, StringBuilder commandLine, IntPtr processAttributes,
            IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string folder,
            ref STARTUPINFO startup, out PROCESS_INFORMATION information);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass,
            ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int length);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool IsProcessInJob(SafeProcessHandle process, SafeFileHandle job, out bool result);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint ResumeThread(IntPtr thread);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
    }
}
