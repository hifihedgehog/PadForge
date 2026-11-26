using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using PadForge.Common;

namespace PadForge.Services
{
    /// <summary>
    /// The DSX.exe stand-in (#529). Some mods made for DSX look for a
    /// process named DSX and turn themselves off without one: DualSense4GTAV
    /// checks on every lights tick and, with its default settings, shows
    /// "This mod will shutdown" (iO.cs:61, showconmes true at
    /// Config/ControllerConfig.cs:17). DualSenseY's first version solved it
    /// the same way (UDP.cs:77, StartFakeDSXProcess).
    ///
    /// <para>The stand-in is PadForge.DsxStandIn, a .NET Framework program of
    /// a few kilobytes that Windows runs on its own framework. PadForge
    /// embeds it, writes it as DSX.exe into DSX in the protected folder's
    /// Work folder (<see cref="Folder"/>) and starts it with its own process
    /// id. It runs only while the game mod server
    /// listens with the switch on: it starts after the bind and stops before
    /// the socket closes, so a mod that sees it always gets its reply. It
    /// sits in a job object that ends it when PadForge ends, and it also
    /// exits by itself when the PadForge that started it does.</para>
    /// </summary>
    internal static class DsxStandIn
    {
        internal const string ResourceName = "PadForge.DsxStandIn.exe";

        /// <summary>The protected folder's DSX folder (#600), where only
        /// Administrators and SYSTEM can write. The stand-in runs with
        /// PadForge's rights, and Windows loads a library or a .config from a
        /// program's own folder first, so the user's temp folder, where it was
        /// before, let any program of the user put code in it. Null in the
        /// test host, which has no protected folder.</summary>
        internal static string Folder => ProtectedWork.Folder("DSX");
        internal static string ExePath => Folder is { } folder ? Path.Combine(folder, "DSX.exe") : null;

        private static readonly object s_lock = new();
        private static Process s_process;
        private static IntPtr s_job;

        /// <summary>The stand-in's process id, 0 when none runs, so the port
        /// files can tell it from a real DSX.</summary>
        internal static int ProcessId
        {
            get
            {
                lock (s_lock)
                {
                    try { return s_process != null && !s_process.HasExited ? s_process.Id : 0; }
                    catch (InvalidOperationException) { return 0; }
                }
            }
        }

        internal static bool IsRunning => ProcessId != 0;

        /// <summary>Starts the stand-in unless it runs. Never throws.</summary>
        internal static void Start()
        {
            lock (s_lock)
            {
                try
                {
                    if (s_process != null)
                    {
                        if (!s_process.HasExited) return;
                        ReleaseLocked();
                    }
                    string exe = ExePath;
                    string path = exe == null ? null : Extract(exe);
                    if (path == null) return;
                    var psi = new ProcessStartInfo(path, Arguments(Environment.ProcessId))
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = Path.GetDirectoryName(path),
                    };
                    Process process;
                    // The file is held open against writes and deletion from
                    // its check through the start, and checked again under
                    // that hold. ShellLaunch is the fenced start: the test host
                    // starts nothing.
                    using (var pin = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        if (!SameAsEmbedded(pin)) return;
                        process = ShellLaunch.StartOwnHelper(psi);
                    }
                    if (process == null) return;
                    s_process = process;
                    s_job = CreateKillOnCloseJob();
                    if (s_job != IntPtr.Zero && !AssignProcessToJobObject(s_job, process.Handle))
                    {
                        CloseHandle(s_job);
                        s_job = IntPtr.Zero;
                    }
                }
                catch
                {
                    // Without the stand-in the server still serves every mod
                    // that does not look for DSX.
                    ReleaseLocked();
                }
            }
        }

        /// <summary>Ends the stand-in. Never throws.</summary>
        internal static void Stop()
        {
            lock (s_lock) ReleaseLocked();
        }

        private static void ReleaseLocked()
        {
            try
            {
                if (s_process != null && !s_process.HasExited) s_process.Kill();
            }
            catch { /* it exited on its own */ }
            try { s_process?.Dispose(); } catch { }
            s_process = null;
            if (s_job != IntPtr.Zero)
            {
                CloseHandle(s_job);
                s_job = IntPtr.Zero;
            }
        }

        /// <summary>The stand-in's only argument: the process it waits on.</summary>
        internal static string Arguments(int parentProcessId)
            => parentProcessId.ToString(CultureInfo.InvariantCulture);

        /// <summary>The embedded program's bytes, or null in a build without it.</summary>
        internal static byte[] ReadEmbedded()
        {
            using var stream = typeof(DsxStandIn).Assembly.GetManifestResourceStream(ResourceName);
            if (stream == null) return null;
            using var copy = new MemoryStream((int)stream.Length);
            stream.CopyTo(copy);
            return copy.ToArray();
        }

        /// <summary>Whether an open file holds the embedded program's
        /// bytes.</summary>
        internal static bool SameAsEmbedded(Stream file)
        {
            var bytes = ReadEmbedded();
            if (bytes == null || file.Length != bytes.Length) return false;
            using var copy = new MemoryStream(bytes.Length);
            file.CopyTo(copy);
            return copy.GetBuffer().AsSpan(0, (int)copy.Length).SequenceEqual(bytes);
        }

        /// <summary>Writes the embedded program to <paramref name="path"/>
        /// unless the same bytes are already there, and returns the path, or
        /// null when it cannot be written.</summary>
        internal static string Extract(string path)
        {
            var bytes = ReadEmbedded();
            if (bytes == null) return null;
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length == bytes.Length
                    && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
                    return path;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, bytes);
                return path;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        // ─────────────────────────────────────────────
        //  The job object
        // ─────────────────────────────────────────────

        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JobObjectLimitKillOnJobClose = 0x2000;

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimitInformationData
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        /// <summary>A job that ends its processes when its last handle
        /// closes, which happens when PadForge ends however it ends.</summary>
        private static IntPtr CreateKillOnCloseJob()
        {
            IntPtr job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return IntPtr.Zero;
            var info = new JobObjectExtendedLimitInformationData();
            info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
            int size = Marshal.SizeOf<JobObjectExtendedLimitInformationData>();
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, buffer, false);
                if (SetInformationJobObject(job, JobObjectExtendedLimitInformation, buffer, (uint)size))
                    return job;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
            CloseHandle(job);
            return IntPtr.Zero;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr attributes, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
