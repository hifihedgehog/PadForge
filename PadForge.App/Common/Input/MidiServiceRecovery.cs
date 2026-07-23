using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace PadForge.Common.Input
{
    /// <summary>
    /// One-shot recovery for a wedged Windows MIDI service. Bench-proven
    /// sequence (2026-07-23): midisrv sat in StopPending forever (SCM
    /// could not stop it) while every endpoint create hung; killing the
    /// process and restarting the service took MIDI slot creation from a
    /// 15 s timeout to 63 ms. A hung create is the wedge signature, so
    /// the create path calls this once per process before giving up.
    /// Restarting midisrv drops other apps' MIDI sessions, which is why
    /// this runs only when the service is already answering nobody.
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

        private static int _attempted;

        /// <summary>Attempts the restart once per process. Returns true
        /// when midisrv is Running again afterward.</summary>
        public static bool TryRecoverOnce()
        {
            if (Interlocked.Exchange(ref _attempted, 1) == 1) return false;
            try { return Recover(); }
            catch { return false; }
        }

        private static bool Recover()
        {
            PadForge.Engine.SdlDiagLog.WriteLine("MIDIRECOVER create hung; one-shot midisrv restart");
            IntPtr scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
            if (scm == IntPtr.Zero) return false;
            try
            {
                IntPtr svc = OpenServiceW(scm, "midisrv", SERVICE_STOP | SERVICE_START | SERVICE_QUERY_STATUS);
                if (svc == IntPtr.Zero) return false;
                try
                {
                    var status = new SERVICE_STATUS();
                    ControlService(svc, SERVICE_CONTROL_STOP, ref status);
                    if (!WaitForState(svc, SERVICE_STOPPED, 5_000, out uint pid))
                    {
                        // Wedged past the SCM (the observed failure mode):
                        // kill the process, then re-confirm stopped.
                        if (pid != 0)
                        {
                            try
                            {
                                using var proc = Process.GetProcessById((int)pid);
                                proc.Kill();
                                proc.WaitForExit(5_000);
                            }
                            catch { /* already gone */ }
                        }
                        if (!WaitForState(svc, SERVICE_STOPPED, 5_000, out _))
                        {
                            PadForge.Engine.SdlDiagLog.WriteLine("MIDIRECOVER midisrv would not stop");
                            return false;
                        }
                    }

                    StartServiceW(svc, 0, IntPtr.Zero);
                    // A post-kill service start exceeds 10 s on this bench
                    // (observed 2026-07-23: Running at ~15 s while a 10 s
                    // wait had already declared failure). 30 s covers it.
                    bool running = WaitForState(svc, SERVICE_RUNNING, 30_000, out _);
                    PadForge.Engine.SdlDiagLog.WriteLine(
                        running ? "MIDIRECOVER midisrv restarted" : "MIDIRECOVER midisrv failed to start");
                    return running;
                }
                finally { CloseServiceHandle(svc); }
            }
            finally { CloseServiceHandle(scm); }
        }

        private static bool WaitForState(IntPtr svc, uint desired, int timeoutMs, out uint pid)
        {
            pid = 0;
            long deadline = Environment.TickCount64 + timeoutMs;
            while (true)
            {
                var sp = new SERVICE_STATUS_PROCESS();
                if (!QueryServiceStatusEx(svc, SC_STATUS_PROCESS_INFO, ref sp,
                        (uint)Marshal.SizeOf<SERVICE_STATUS_PROCESS>(), out _))
                    return false;
                pid = sp.dwProcessId;
                if (sp.dwCurrentState == desired) return true;
                if (Environment.TickCount64 >= deadline) return false;
                Thread.Sleep(250);
            }
        }
    }
}
