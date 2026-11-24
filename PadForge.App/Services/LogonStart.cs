using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using PadForge.Common;

namespace PadForge.Services
{
    /// <summary>
    /// The logon start (#597). The Start at Login task runs as SYSTEM, whose
    /// environment Task Scheduler builds from SYSTEM's own profile, and starts
    /// PadForge.exe with <see cref="Switch"/> and the SID of the user who
    /// turned the setting on. This run, before any of WPF loads, finds that
    /// user's session, takes the user's token (WTSQueryUserToken), takes the
    /// linked token with the full rights when Windows split the user's token
    /// at logon, builds the variables the app gets itself, and starts
    /// PadForge in that session on the interactive desktop.
    ///
    /// <para>Before #597 the task ran PadForge as the user, with rights, and
    /// Task Scheduler gave it the variables of the user's profile. Every
    /// program of the user can write those (HKCU\Environment), and the .NET
    /// runtime loads the library CORECLR_PROFILER_PATH names before Main when
    /// CORECLR_ENABLE_PROFILING is 1 (profilinghelper.cpp). Start at Login
    /// then gave any program of the user administrator rights at the next
    /// logon, with no prompt. Here the app's variables come from an allow
    /// list (<see cref="RightsVariables"/>): the machine's own values for
    /// the system's variables, the user's for the user's folders, and the
    /// PadForge variables the diagnostics read. Every other one is dropped,
    /// CORECLR_*, COR_*, DOTNET_*, COMPlus_* and SDL3_DYNAMIC_API among them,
    /// and DOTNET_BUNDLE_EXTRACT_BASE_DIR is set to the protected unpack
    /// folder, so the app needs no second start (<see cref="ProtectedStart"/>).</para>
    ///
    /// <para>A user whose token Windows did not split gets the token as it
    /// is, which is what the task gave before at its highest run level: an
    /// administrator with UAC off runs with rights, and a user who is no
    /// administrator runs without them, and PadForge asks Windows for them as
    /// on any start (<see cref="StartWithRights"/>).</para>
    ///
    /// <para>The app leaves the task's job object (CREATE_BREAKAWAY_FROM_JOB),
    /// so ending the task, or Task Scheduler's end of a task that outlived a
    /// time limit, never ends PadForge. Its priority class is the normal one,
    /// whatever the task's priority gives this run.</para>
    /// </summary>
    internal static class LogonStart
    {
        internal const string Switch = "--logon-start";

        /// <summary>The run's exit codes, which Task Scheduler shows as the
        /// task's last result.</summary>
        internal const int Started = 0, BadArguments = 2, NotSystem = 3, NoSession = 4, NoToken = 5, NotLaunched = 6;

        /// <summary>How long the run looks for the user's session. The logon
        /// trigger fires while the logon is under way.</summary>
        internal const int SessionWaitMs = 120_000;
        internal const int SessionLookEveryMs = 500;

        internal const string SystemSid = "S-1-5-18";

        /// <summary>Whether the command line is the task's: the switch and a
        /// SID.</summary>
        internal static bool IsRun(string[] args) => args is { Length: 2 } && args[0] == Switch;

        /// <summary>The whole run, as SYSTEM: PadForge.exe started for the
        /// user <c>args[1]</c> names.</summary>
        internal static int Run(string[] args)
        {
            if (!IsRun(args) || !LooksLikeSid(args[1])) return BadArguments;
            string exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return NotLaunched;
            string folder = System.IO.Path.GetDirectoryName(exe);
            return Launch(args[1], exe, Quote(exe), folder, ProtectedFolder.Natives, out _);
        }

        /// <summary>What a launch found, for the probe and the log.</summary>
        internal sealed class Report
        {
            public uint Session;
            public int ElevationType;
            public bool UsedLinkedToken;
            public bool BrokeAway;
            public int ProcessId;
            public int Error;

            /// <summary>The names of the user's variables that were left
            /// out.</summary>
            public List<string> Dropped = new();
        }

        /// <summary>
        /// Starts <paramref name="exe"/> with <paramref name="commandLine"/> in
        /// <paramref name="folder"/> for the user <paramref name="sid"/> names,
        /// in that user's session, with the variables
        /// <see cref="RightsVariables.Build"/> makes from the machine's block and
        /// the user's, and DOTNET_BUNDLE_EXTRACT_BASE_DIR set to
        /// <paramref name="natives"/>. Returns one of the exit codes.
        /// Only SYSTEM may do it: WTSQueryUserToken needs SeTcbPrivilege
        /// (Microsoft, WTSQueryUserToken).
        /// </summary>
        internal static int Launch(string sid, string exe, string commandLine, string folder, string natives, out Report report)
        {
            report = new Report();
            if (!LooksLikeSid(sid) || string.IsNullOrEmpty(natives)) return BadArguments;
            if (!string.Equals(PadForge.Services.Automation.PipeOwner.OwnUser, SystemSid, StringComparison.Ordinal)) return NotSystem;

            // SYSTEM holds these privileges, and a privilege must be enabled
            // to count. WTSQueryUserToken and a token's session need
            // SeTcbPrivilege, CreateProcessAsUser SeAssignPrimaryTokenPrivilege
            // and SeIncreaseQuotaPrivilege (Microsoft, CreateProcessAsUserW).
            using var held = Privileges.Hold("SeTcbPrivilege", "SeAssignPrimaryTokenPrivilege", "SeIncreaseQuotaPrivilege");

            uint session = 0;
            IntPtr userToken = IntPtr.Zero;
            long deadline = Environment.TickCount64 + SessionWaitMs;
            while (true)
            {
                if (FindSession(sid, out session, out userToken)) break;
                if (Environment.TickCount64 >= deadline) return NoSession;
                Thread.Sleep(SessionLookEveryMs);
            }
            report.Session = session;

            IntPtr token = IntPtr.Zero;
            try
            {
                token = TokenToRun(userToken, session, report);
                if (token == IntPtr.Zero)
                {
                    report.Error = Marshal.GetLastWin32Error();
                    return NoToken;
                }
                string[] system = System32Calls.MachineVariables();
                string[] user = system == null ? null : System32Calls.VariablesOf(token);
                if (system == null || user == null)
                {
                    report.Error = Marshal.GetLastSystemError();
                    return NoToken;
                }
                var kept = RightsVariables.Build(system, user, natives);
                foreach (string variable in user)
                {
                    string name = ProtectedStart.NameOf(variable);
                    if (name.Length > 0 && !kept.Exists(k => string.Equals(ProtectedStart.NameOf(k), name, StringComparison.OrdinalIgnoreCase)))
                        report.Dropped.Add(name);
                }
                string block = RightsVariables.ToBlock(kept);

                var startup = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>(), lpDesktop = @"winsta0\default" };
                char[] line = (commandLine + "\0").ToCharArray();
                uint flags = CreateUnicodeEnvironment | NormalPriorityClass;
                bool started = CreateProcessAsUserW(token, exe, line, IntPtr.Zero, IntPtr.Zero, false,
                    flags | CreateBreakawayFromJob, block, folder, ref startup, out var child);
                report.BrokeAway = started;
                if (!started)
                {
                    // A job that does not allow breakaway refuses the flag
                    // with ERROR_ACCESS_DENIED (Microsoft, Process Creation
                    // Flags). Task Scheduler's allows it on the Windows this
                    // was measured on, and the app still starts on one that
                    // does not.
                    int first = Marshal.GetLastWin32Error();
                    if (first != ErrorAccessDenied)
                    {
                        report.Error = first;
                        return NotLaunched;
                    }
                    started = CreateProcessAsUserW(token, exe, line, IntPtr.Zero, IntPtr.Zero, false,
                        flags, block, folder, ref startup, out child);
                    if (!started)
                    {
                        report.Error = Marshal.GetLastWin32Error();
                        return NotLaunched;
                    }
                }
                report.ProcessId = child.dwProcessId;
                CloseHandle(child.hThread);
                CloseHandle(child.hProcess);
                return Started;
            }
            finally
            {
                if (token != IntPtr.Zero && token != userToken) CloseHandle(token);
                if (userToken != IntPtr.Zero) CloseHandle(userToken);
            }
        }

        // ── The task ──

        /// <summary>
        /// The task's action, for cmd.exe: the unpack folder set, then
        /// PadForge.exe with <see cref="Switch"/> and the user's SID, only
        /// while the exe has the size of the one that registered the task.
        /// An older PadForge has no such switch and would start the whole app
        /// as SYSTEM, so a changed exe starts nothing, and its next start with
        /// rights makes the task again (<see cref="StartupHelper.AuditTask"/>).
        /// The give-back tasks' action holds its runs the same way
        /// (<see cref="GiveBackTask.Action"/>). Null when a path cannot stand
        /// in quotes.
        /// </summary>
        internal static string Action(string exe, string sid, string natives, long size)
        {
            if (!GiveBackTask.CanQuote(exe) || !GiveBackTask.CanQuote(natives) || !LooksLikeSid(sid) || size < 0) return null;
            return $"set \"{ProtectedStart.BaseVariable}={natives}\" & "
                   + $"for %I in (\"{exe}\") do if \"%~zI\"==\"{size}\" ( \"{exe}\" {Switch} {sid} )";
        }

        /// <summary>
        /// The task's definition: a logon trigger for the user
        /// <paramref name="sid"/> names, run as SYSTEM (Microsoft,
        /// logonTrigger UserId: the task starts at that user's logon alone),
        /// cmd.exe running <paramref name="command"/> with its line in one
        /// pair of quotes (/s), and no AutoRun (/d). One instance at a time,
        /// no time limit (PT0S), started and kept on battery, with no idle
        /// condition, at the normal priority (4), so the run's own priority
        /// is a normal one. With <paramref name="trigger"/> false it has no
        /// trigger, as the probe registers it.
        /// </summary>
        internal static string TaskXml(string sid, string command, string system32, bool trigger = true)
        {
            string program = System.Security.SecurityElement.Escape(System.IO.Path.Combine(system32, "cmd.exe"));
            string arguments = System.Security.SecurityElement.Escape($"/q /d /s /c \"{command}\"");
            string user = System.Security.SecurityElement.Escape(sid);
            var xml = new StringBuilder();
            void Line(string text) => xml.Append(text).Append("\r\n");
            Line("<?xml version=\"1.0\" encoding=\"UTF-16\"?>");
            Line("<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">");
            Line("  <RegistrationInfo>");
            Line("    <Description>Starts PadForge when this user signs in to Windows.</Description>");
            Line("  </RegistrationInfo>");
            if (trigger)
            {
                Line("  <Triggers>");
                Line("    <LogonTrigger>");
                Line("      <Enabled>true</Enabled>");
                Line($"      <UserId>{user}</UserId>");
                Line("    </LogonTrigger>");
                Line("  </Triggers>");
            }
            else
            {
                Line("  <Triggers />");
            }
            Line("  <Principals>");
            Line("    <Principal id=\"LocalSystem\">");
            Line($"      <UserId>{SystemSid}</UserId>");
            Line("      <RunLevel>HighestAvailable</RunLevel>");
            Line("    </Principal>");
            Line("  </Principals>");
            Line("  <Settings>");
            Line("    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>");
            Line("    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>");
            Line("    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>");
            Line("    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>");
            Line("    <IdleSettings>");
            Line("      <StopOnIdleEnd>false</StopOnIdleEnd>");
            Line("      <RestartOnIdle>false</RestartOnIdle>");
            Line("    </IdleSettings>");
            Line("    <RunOnlyIfIdle>false</RunOnlyIfIdle>");
            Line("    <Priority>4</Priority>");
            Line("    <Enabled>true</Enabled>");
            Line("  </Settings>");
            Line("  <Actions Context=\"LocalSystem\">");
            Line("    <Exec>");
            Line($"      <Command>{program}</Command>");
            Line($"      <Arguments>{arguments}</Arguments>");
            Line("    </Exec>");
            Line("  </Actions>");
            Line("</Task>");
            return xml.ToString();
        }

        // ── The pieces, each a test's to check ──

        /// <summary>Whether text has a SID's form: S-1- then digits and
        /// dashes.</summary>
        internal static bool LooksLikeSid(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length > 184 || !text.StartsWith("S-1-", StringComparison.Ordinal)) return false;
            for (int i = 4; i < text.Length; i++)
                if (!(text[i] >= '0' && text[i] <= '9') && text[i] != '-') return false;
            return text[^1] != '-';
        }

        /// <summary>A session and its user, as Windows lists them.</summary>
        internal readonly record struct SessionEntry(uint Id, bool Active, long LogonTime, string UserSid);

        /// <summary>The session to start in: one of the user's, the active
        /// one first, then the latest logon. Null when the user has none.</summary>
        internal static uint? PickSession(IEnumerable<SessionEntry> sessions, string sid)
        {
            SessionEntry? best = null;
            foreach (var s in sessions)
            {
                if (!string.Equals(s.UserSid, sid, StringComparison.OrdinalIgnoreCase)) continue;
                if (best == null || (s.Active && !best.Value.Active)
                    || (s.Active == best.Value.Active && s.LogonTime > best.Value.LogonTime))
                    best = s;
            }
            return best?.Id;
        }

        /// <summary>A path in quotes, for a command line's program.</summary>
        internal static string Quote(string path) => "\"" + path + "\"";

        // ── Windows ──

        private static bool FindSession(string sid, out uint session, out IntPtr userToken)
        {
            session = 0;
            userToken = IntPtr.Zero;
            var found = new List<SessionEntry>();
            if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out IntPtr list, out int count)) return false;
            try
            {
                int size = Marshal.SizeOf<WtsSessionInfo>();
                for (int i = 0; i < count; i++)
                {
                    var info = Marshal.PtrToStructure<WtsSessionInfo>(list + i * size);
                    if (!WTSQueryUserToken((uint)info.SessionId, out IntPtr t)) continue;
                    try
                    {
                        string user = PadForge.Services.Automation.AutomationClientIdentity.ReadUser(t);
                        if (user != null)
                            found.Add(new SessionEntry((uint)info.SessionId, info.State == WtsActive, LogonTime((uint)info.SessionId), user));
                    }
                    finally
                    {
                        CloseHandle(t);
                    }
                }
            }
            finally
            {
                WTSFreeMemory(list);
            }
            uint? pick = PickSession(found, sid);
            if (pick == null) return false;
            session = pick.Value;
            return WTSQueryUserToken(session, out userToken);
        }

        /// <summary>The token the app runs with, a primary token for
        /// <paramref name="session"/>: the linked token when Windows split
        /// the user's token (TokenElevationTypeLimited), the token itself
        /// otherwise. With SeTcbPrivilege the linked token comes back as a
        /// primary token (Microsoft, TOKEN_LINKED_TOKEN). Zero on
        /// failure.</summary>
        private static IntPtr TokenToRun(IntPtr userToken, uint session, Report report)
        {
            IntPtr source = userToken;
            IntPtr linked = IntPtr.Zero;
            try
            {
                int type = PadForge.Services.Automation.AutomationClientIdentity.ReadInt(userToken, TokenElevationTypeClass) ?? 0;
                report.ElevationType = type;
                if (type == TokenElevationTypeLimited)
                {
                    if (!GetTokenInformation(userToken, TokenLinkedToken, out linked, IntPtr.Size, out _) || linked == IntPtr.Zero)
                        return IntPtr.Zero;
                    source = linked;
                    report.UsedLinkedToken = true;
                }
                if (!DuplicateTokenEx(source, MaximumAllowed, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out IntPtr primary))
                    return IntPtr.Zero;
                if (!SetTokenInformation(primary, TokenSessionId, ref session, sizeof(uint)))
                {
                    CloseHandle(primary);
                    return IntPtr.Zero;
                }
                return primary;
            }
            finally
            {
                if (linked != IntPtr.Zero) CloseHandle(linked);
            }
        }

        private static long LogonTime(uint session)
        {
            if (!WTSQuerySessionInformationW(IntPtr.Zero, session, WtsSessionInfoClass, out IntPtr buffer, out int bytes)) return 0;
            try
            {
                // WTSINFOW: State, SessionId, IncomingBytes, OutgoingBytes,
                // IncomingFrames, OutgoingFrames, IncomingCompressedBytes,
                // OutgoingCompressedBytes (eight DWORDs), WinStationName[32],
                // Domain[17], UserName[21] as WCHARs, then the times as
                // LARGE_INTEGERs, ConnectTime, DisconnectTime, LastInputTime,
                // LogonTime, CurrentTime (Microsoft, WTSINFOW). The names
                // end at byte 172 and the times start on the next multiple
                // of 8.
                const int NamesEnd = 8 * 4 + (32 + 17 + 21) * 2;
                const int LogonTimeOffset = (NamesEnd + 7) / 8 * 8 + 3 * 8;
                return bytes >= LogonTimeOffset + 8 ? Marshal.ReadInt64(buffer, LogonTimeOffset) : 0;
            }
            finally
            {
                WTSFreeMemory(buffer);
            }
        }

        private const int WtsActive = 0;
        private const int WtsSessionInfoClass = 24;
        private const int TokenSessionId = 12;
        private const int TokenElevationTypeClass = 18;
        private const int TokenLinkedToken = 19;
        internal const int TokenElevationTypeDefault = 1, TokenElevationTypeFull = 2, TokenElevationTypeLimited = 3;
        private const uint MaximumAllowed = 0x02000000;
        private const int SecurityImpersonation = 2;
        private const int TokenPrimary = 1;
        internal const uint CreateUnicodeEnvironment = 0x00000400;
        internal const uint CreateBreakawayFromJob = 0x01000000;
        internal const uint NormalPriorityClass = 0x00000020;
        private const int ErrorAccessDenied = 5;

        [StructLayout(LayoutKind.Sequential)]
        private struct WtsSessionInfo
        {
            public int SessionId;
            public IntPtr WinStationName;
            public int State;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSEnumerateSessionsW(IntPtr server, int reserved, int version, out IntPtr sessions, out int count);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSQueryUserToken(uint session, out IntPtr token);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSQuerySessionInformationW(IntPtr server, uint session, int infoClass, out IntPtr buffer, out int bytes);

        [DllImport("wtsapi32.dll")]
        private static extern void WTSFreeMemory(IntPtr memory);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr token, int infoClass, out IntPtr info, int length, out int returned);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool SetTokenInformation(IntPtr token, int infoClass, ref uint info, int length);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attributes, int level, int type, out IntPtr duplicate);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessAsUserW(IntPtr token, string application, char[] commandLine, IntPtr processAttributes,
            IntPtr threadAttributes, bool inheritHandles, uint flags, string environment, string folder, ref StartupInfo startup,
            out ProcessInformation process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
