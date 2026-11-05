using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace PadForge.Common.Input
{
    /// <summary>The service refused a MIDI session outright, which is what a
    /// stopped or broken MIDI service answers. The create path asks for a
    /// service restart on it and creates once more, the same as for a hung
    /// create.</summary>
    internal sealed class MidiSessionUnavailableException : InvalidOperationException
    {
        internal MidiSessionUnavailableException() : base("Failed to create MIDI session.") { }
    }

    /// <summary>No MIDI API answered on the service instance a create's own
    /// restart brought up, within the create's bound. The create failed for
    /// want of an API, so its slot tries again once a probe answers.</summary>
    internal sealed class MidiServiceUnavailableException : TimeoutException
    {
        internal MidiServiceUnavailableException(string deviceName)
            : base($"Windows MIDI Services stayed unavailable after a service restart while creating '{deviceName}'.") { }
    }

    /// <summary>What a midisrv restart did.</summary>
    internal enum MidiServiceRestart
    {
        /// <summary>Not attempted: another restart's cooldown is running, or
        /// a restart cannot help (<see cref="MidiVirtualController"/> asks
        /// first).</summary>
        Refused,

        /// <summary>The instance that was running still is: the service
        /// could not be opened or stopped, or it was not running and did not
        /// start.</summary>
        Unchanged,

        /// <summary>The instance that was running ended, but no new one was
        /// seen running within the wait. Everything opened on the old one is
        /// gone all the same.</summary>
        EndedNotRunning,

        /// <summary>Running again after the restart.</summary>
        Running,
    }

    /// <summary>
    /// Recovery for a wedged Windows MIDI service. Bench-proven sequence
    /// (2026-07-23): midisrv sat in StopPending forever (SCM could not stop
    /// it) while every endpoint create hung. Killing the process and
    /// restarting the service took MIDI slot creation from a 15 s timeout to
    /// 63 ms. Two signatures ask for the restart. One is a MIDI call still
    /// pending past its bound: a create, the availability probe, an input
    /// session or an input connection at the create path's 15 s
    /// (<see cref="MidiVirtualController.ConnectTimeoutMs"/>), the input
    /// endpoint query or a legacy port listing at 10 s, and the release
    /// before a runtime uninstall once the call that waits on it gives up
    /// (<see cref="MidiRuntimeSwitch.ReleaseTimeoutMs"/>, 60 s) and its
    /// worker has itself been at work for the create path's 15 s, unless a
    /// hung probe is what that release waits behind. The other is a session
    /// the service refuses, for a create or for MIDI input. Each asks
    /// through <see cref="MidiVirtualController.RestartService"/>, and the
    /// probe through its own recovery. Both first rule out a call no restart
    /// can reach (<see cref="MidiVirtualController.ServiceRestartCanHelp"/>),
    /// and every path shares one restart per cooldown window
    /// (<see cref="CooldownMs"/>). Restarting midisrv drops other apps'
    /// MIDI sessions, so a call that is only pending asks for it at its
    /// bound and no sooner.
    /// </summary>
    internal static class MidiServiceRecovery
    {
        private const uint SC_MANAGER_CONNECT = 0x0001;
        private const uint SERVICE_QUERY_STATUS = 0x0004;
        private const uint SERVICE_START = 0x0010;
        private const uint SERVICE_STOP = 0x0020;
        private const uint SERVICE_CONTROL_STOP = 0x00000001;
        private const uint SERVICE_STOPPED = 0x00000001;
        private const uint SERVICE_RUNNING = 0x00000004;
        private const int SC_STATUS_PROCESS_INFO = 0;
        private const string ServiceName = "midisrv";

        [StructLayout(LayoutKind.Sequential)]
        private struct SERVICE_STATUS
        {
            public uint dwServiceType;
            public uint dwCurrentState;
            public uint dwControlsAccepted;
            public uint dwWin32ExitCode;
            public uint dwServiceSpecificExitCode;
            public uint dwCheckPoint;
            public uint dwWaitHint;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SERVICE_STATUS_PROCESS
        {
            public uint dwServiceType;
            public uint dwCurrentState;
            public uint dwControlsAccepted;
            public uint dwWin32ExitCode;
            public uint dwServiceSpecificExitCode;
            public uint dwCheckPoint;
            public uint dwWaitHint;
            public uint dwProcessId;
            public uint dwServiceFlags;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenSCManagerW(string machineName, string databaseName, uint access);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenServiceW(IntPtr scManager, string serviceName, uint access);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool ControlService(IntPtr service, uint control, ref SERVICE_STATUS status);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool QueryServiceStatusEx(IntPtr service, int infoLevel, ref SERVICE_STATUS_PROCESS status, uint bufSize, out uint bytesNeeded);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool StartServiceW(IntPtr service, uint numArgs, IntPtr args);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool CloseServiceHandle(IntPtr handle);

        // Cooldown, not once-per-process: teardown of a live endpoint can
        // wedge the service again later in the same session (observed
        // twice in one run, 2026-07-23), and a once-only gate left the
        // second wedge with no rescue. The cooldown still prevents
        // restart storms if the service is truly unrecoverable. Tests
        // shorten it.
        internal static int CooldownMs = 120_000;
        private static long _lastAttemptTick = -CooldownMs;

        /// <summary>Test seam: stands in for the restart, true for a service
        /// running again and false for one left as it was. Null in
        /// production.</summary>
        internal static Func<bool> RecoverSeam;

        /// <summary>Test seam: stands in for the restart with its full
        /// outcome. Wins over <see cref="RecoverSeam"/>. Null in
        /// production.</summary>
        internal static Func<MidiServiceRestart> RestartSeam;

        /// <summary>Test seam: stands in for the service's process id. Null
        /// in production.</summary>
        internal static Func<uint> ProcessIdSeam;

        /// <summary>Test seam: set by the test assembly for the life of its
        /// process. A restart neither seam stands in for then changes nothing
        /// instead of reaching this PC's midisrv. A restart request can
        /// outlive the test that caused it (a watchdog's worker, an input
        /// call's timer) and run once that test has put its seams away. The
        /// restart itself (<see cref="Recover"/>) asks again as it begins,
        /// so a change to the lines that pick a seam cannot send a test to
        /// the real service. False in production, where nothing sets
        /// it.</summary>
        internal static bool SeamsOnlyForTest;

        /// <summary>Lets a test attempt a restart again at once.</summary>
        internal static void ResetCooldownForTest() => Interlocked.Exchange(ref _lastAttemptTick, -CooldownMs);

        /// <summary>Test seam: stands in for whether this PC has the MIDI
        /// service. Null in production.</summary>
        internal static Func<bool> ServiceExistsSeam;

        private const int ERROR_SERVICE_DOES_NOT_EXIST = 1060;

        // Set once the service is found. A PC without it is asked again on
        // the next read, in case an update has installed it since.
        private static volatile bool s_serviceFound;

        // Set when the last read of ServiceExists answered no.
        private static volatile bool s_serviceMissing;

        /// <summary>Whether this PC has the MIDI service at all, by the
        /// service manager's answer to an open of it for a status read. The
        /// build and the API mode do not say: a Windows 11 24H2 or later PC
        /// can run without it, and its WinMM then talks to classic drivers
        /// alone. Only a service the manager says does not exist reads as
        /// missing. One it cannot open for another reason reads as there,
        /// as before.</summary>
        internal static bool ServiceExists
        {
            get
            {
                var seam = ServiceExistsSeam;
                if (seam != null)
                {
                    bool answer = seam();
                    s_serviceMissing = !answer;
                    return answer;
                }
                if (s_serviceFound) return true;
                bool exists;
                try { exists = ReadServiceExists(); }
                catch { exists = true; }
                if (exists) s_serviceFound = true;
                s_serviceMissing = !exists;
                return exists;
            }
        }

        /// <summary>The last answer <see cref="ServiceExists"/> gave, with no
        /// call to the service manager, for the poll thread's watchdogs, which
        /// fire while something is already hung behind the MIDI service. The
        /// workers they watch ask first. Before any answer the service reads as
        /// there, as one the manager cannot be asked about does.</summary>
        internal static bool ServiceLastSeen => !s_serviceMissing;

        /// <summary>Forgets the last answer, for a test that changes the
        /// PC it stands in for.</summary>
        internal static void ForgetServiceAnswerForTest() => s_serviceMissing = false;

        private static bool ReadServiceExists()
        {
            IntPtr scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
            if (scm == IntPtr.Zero) return true;
            try
            {
                IntPtr svc = OpenServiceW(scm, ServiceName, SERVICE_QUERY_STATUS);
                if (svc != IntPtr.Zero)
                {
                    CloseServiceHandle(svc);
                    return true;
                }
                return Marshal.GetLastWin32Error() != ERROR_SERVICE_DOES_NOT_EXIST;
            }
            finally { CloseServiceHandle(scm); }
        }

        /// <summary>How much of the cooldown is left before another restart
        /// may be attempted, or 0.</summary>
        internal static int CooldownRemainingMs
        {
            get
            {
                long left = CooldownMs - (Environment.TickCount64 - Interlocked.Read(ref _lastAttemptTick));
                return left <= 0 ? 0 : (int)Math.Min(left, int.MaxValue);
            }
        }

        // The restarts that got past the cooldown: how many have begun, and
        // how many are still in flight.
        private static int s_restartsBegun;
        private static int s_restartsInFlight;

        /// <summary>How many restarts have begun. The availability probe
        /// reads it before its starts and again before it commits: a restart
        /// that began in between means the answer straddles it, the old
        /// instance on one side and the new one on the other, and the probe
        /// keeps nothing.</summary>
        internal static int RestartsBegun => Volatile.Read(ref s_restartsBegun);

        /// <summary>Whether a restart is in flight, through whatever its
        /// caller runs before it ends (<see cref="TryRestart"/>'s
        /// <c>whileInFlight</c>). Nothing probes the service meanwhile: it is
        /// stopping or starting, and a Windows MIDI Services API that refuses
        /// there would leave the legacy API in its place.</summary>
        internal static bool RestartInFlight => Volatile.Read(ref s_restartsInFlight) != 0;

        /// <summary>Attempts the restart, at most once per cooldown window,
        /// and says what it did. A caller starts a new MIDI generation
        /// whenever the old instance ended, running again or not, since
        /// everything opened on that instance is gone.</summary>
        /// <param name="trigger">What showed the service wedged, for the
        /// diagnostics log.</param>
        /// <param name="whileInFlight">Runs with the outcome before the
        /// restart reads as over, for the reset that follows it, so no probe
        /// and no instance check lands between the restart and its
        /// reset.</param>
        public static MidiServiceRestart TryRestart(string trigger, Action<MidiServiceRestart> whileInFlight = null)
        {
            long now = Environment.TickCount64;
            long last = Interlocked.Read(ref _lastAttemptTick);
            if (now - last < CooldownMs) return MidiServiceRestart.Refused;
            if (Interlocked.CompareExchange(ref _lastAttemptTick, now, last) != last) return MidiServiceRestart.Refused;
            // In flight first, so a probe that reads the count after this
            // restart began also sees it in flight.
            Interlocked.Increment(ref s_restartsInFlight);
            Interlocked.Increment(ref s_restartsBegun);
            try
            {
                MidiServiceRestart outcome;
                try
                {
                    var restartSeam = RestartSeam;
                    var recoverSeam = RecoverSeam;
                    outcome = restartSeam != null ? restartSeam()
                        : recoverSeam != null ? (recoverSeam() ? MidiServiceRestart.Running : MidiServiceRestart.Unchanged)
                        : SeamsOnlyForTest ? MidiServiceRestart.Unchanged
                        : Recover(trigger);
                }
                catch
                {
                    outcome = MidiServiceRestart.Unchanged;
                }
                try { whileInFlight?.Invoke(outcome); }
                catch { /* best effort, as the restart itself */ }
                return outcome;
            }
            finally
            {
                Interlocked.Decrement(ref s_restartsInFlight);
            }
        }

        private static MidiServiceRestart Recover(string trigger)
        {
            // The service's stop, its process's end and its start all sit
            // below this line, and none of them runs in the test host.
            if (SeamsOnlyForTest) return MidiServiceRestart.Unchanged;
            PadForge.Engine.SdlDiagLog.WriteLine($"MIDIRECOVER {trigger}: midisrv restart");
            IntPtr scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
            if (scm == IntPtr.Zero) return MidiServiceRestart.Unchanged;
            try
            {
                IntPtr svc = OpenServiceW(scm, ServiceName, SERVICE_STOP | SERVICE_START | SERVICE_QUERY_STATUS);
                if (svc == IntPtr.Zero) return MidiServiceRestart.Unchanged;
                try
                {
                    // A service that was not running has no instance to end,
                    // so a start that fails then changes nothing.
                    bool wasRunning = QueryState(svc, out uint state, out _) && state != SERVICE_STOPPED;
                    var status = new SERVICE_STATUS();
                    ControlService(svc, SERVICE_CONTROL_STOP, ref status);
                    if (!WaitForState(svc, SERVICE_STOPPED, 5_000, out uint pid))
                    {
                        // Wedged past the SCM (the observed failure mode):
                        // kill the process, then re-confirm stopped.
                        bool killed = false;
                        if (pid != 0)
                        {
                            try
                            {
                                using var proc = Process.GetProcessById((int)pid);
                                proc.Kill();
                                killed = proc.WaitForExit(5_000);
                            }
                            catch (ArgumentException)
                            {
                                // Already gone.
                                killed = true;
                            }
                            catch { /* the stop wait below decides */ }
                        }
                        if (!WaitForState(svc, SERVICE_STOPPED, 5_000, out _))
                        {
                            PadForge.Engine.SdlDiagLog.WriteLine("MIDIRECOVER midisrv would not stop");
                            return killed ? MidiServiceRestart.EndedNotRunning : MidiServiceRestart.Unchanged;
                        }
                    }

                    StartServiceW(svc, 0, IntPtr.Zero);
                    // A post-kill service start exceeds 10 s on this bench
                    // (observed 2026-07-23: Running at ~15 s while a 10 s
                    // wait had already declared failure). 30 s covers it.
                    bool running = WaitForState(svc, SERVICE_RUNNING, 30_000, out _);
                    PadForge.Engine.SdlDiagLog.WriteLine(
                        running ? "MIDIRECOVER midisrv restarted" : "MIDIRECOVER midisrv failed to start");
                    if (running) return MidiServiceRestart.Running;
                    return wasRunning ? MidiServiceRestart.EndedNotRunning : MidiServiceRestart.Unchanged;
                }
                finally { CloseServiceHandle(svc); }
            }
            finally { CloseServiceHandle(scm); }
        }

        private const uint SYNCHRONIZE = 0x00100000;
        private const uint WAIT_OBJECT_0 = 0;

        /// <summary>The event midisrv sets once it has loaded every transport
        /// at start (MidiSrv.cpp DeviceManagerInitializeWorker). A new
        /// instance resets it as it starts. Microsoft's client waits on it
        /// for at most 10 s before it answers a connectivity check, and then
        /// answers whether or not it was set (MidiSrvTransport.cpp
        /// VerifyConnectivity, g_midiSynchronizedStartTimeoutMs).</summary>
        private const string TransportsLoadedEvent = @"Global\Microsoft-Windows-MidiSrv-DeviceEnumerationComplete";

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeWaitHandle OpenEventW(uint access, bool inheritHandle, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);

        /// <summary>Test seam: stands in for <see cref="TransportsLoaded"/>.
        /// Null in production.</summary>
        internal static Func<int, bool?> TransportsLoadedSeam;

        /// <summary>Waits up to <paramref name="ms"/> for the running service
        /// to finish loading its transports: true once it has, false while
        /// it is still loading them, and null when there is no such event to
        /// read, on a service without it or with none running. A virtual
        /// device create sent before the virtual transport is loaded fails,
        /// since the service has no configuration manager for it yet
        /// (MidiDeviceManager.cpp UpdateTransportConfiguration). The event is
        /// opened for SYNCHRONIZE alone, the right the service grants its
        /// clients: an open that also asks to modify it is refused.</summary>
        internal static bool? TransportsLoaded(int ms)
        {
            var seam = TransportsLoadedSeam;
            if (seam != null) return seam(ms);
            if (SeamsOnlyForTest) return null;
            try
            {
                using var loaded = OpenEventW(SYNCHRONIZE, false, TransportsLoadedEvent);
                if (loaded.IsInvalid) return null;
                return WaitForSingleObject(loaded, (uint)Math.Max(0, ms)) == WAIT_OBJECT_0;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>The running midisrv's process id, or 0 when it is not
        /// running or cannot be read. A new id means a new instance, and
        /// everything opened on the old one is gone.</summary>
        internal static uint ServiceProcessId()
        {
            var seam = ProcessIdSeam;
            if (seam != null) return seam();
            try
            {
                IntPtr scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
                if (scm == IntPtr.Zero) return 0;
                try
                {
                    IntPtr svc = OpenServiceW(scm, ServiceName, SERVICE_QUERY_STATUS);
                    if (svc == IntPtr.Zero) return 0;
                    try
                    {
                        return QueryState(svc, out uint state, out uint pid) && state == SERVICE_RUNNING ? pid : 0;
                    }
                    finally { CloseServiceHandle(svc); }
                }
                finally { CloseServiceHandle(scm); }
            }
            catch
            {
                return 0;
            }
        }

        private static bool QueryState(IntPtr svc, out uint state, out uint pid)
        {
            var sp = new SERVICE_STATUS_PROCESS();
            bool ok = QueryServiceStatusEx(svc, SC_STATUS_PROCESS_INFO, ref sp,
                (uint)Marshal.SizeOf<SERVICE_STATUS_PROCESS>(), out _);
            state = ok ? sp.dwCurrentState : 0;
            pid = ok ? sp.dwProcessId : 0;
            return ok;
        }

        private static bool WaitForState(IntPtr svc, uint desired, int timeoutMs, out uint pid)
        {
            pid = 0;
            long deadline = Environment.TickCount64 + timeoutMs;
            while (true)
            {
                if (!QueryState(svc, out uint state, out pid))
                    return false;
                if (state == desired) return true;
                if (Environment.TickCount64 >= deadline) return false;
                Thread.Sleep(250);
            }
        }
    }
}
