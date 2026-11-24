using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace PadForge.Services.Automation
{
    /// <summary>What Windows says of a pipe's client in the token it hands
    /// the server: whose it is, in which logon session it runs, at which
    /// integrity level, and whether it is an app container.</summary>
    /// <param name="UserSid">The user's SID as text, or null when the token
    /// could not be read.</param>
    /// <param name="SessionId">The Windows session.</param>
    /// <param name="Integrity">The integrity level's RID: 0x2000 is medium,
    /// 0x3000 high, 0x1000 low.</param>
    /// <param name="AppContainer">Whether the token is an app
    /// container's.</param>
    internal readonly record struct AutomationClientToken(string UserSid, int SessionId, int Integrity, bool AppContainer)
    {
        internal const int MediumIntegrity = 0x2000;
    }

    /// <summary>
    /// Who is at the other end of the Automation pipe (#597). The decision is
    /// made from the token Windows hands the server, never from the process
    /// ID, which a program can stage (Project Zero, 2019). The server
    /// impersonates the client for the read and reads the user, the session,
    /// the integrity level and the app container flag from the thread's
    /// token.
    /// </summary>
    internal static class AutomationClientIdentity
    {
        /// <summary>The token's facts, or null when the client could not be
        /// impersonated or its token read.</summary>
        internal static AutomationClientToken? Read(NamedPipeServerStream server)
        {
            AutomationClientToken? facts = null;
            try
            {
                server.RunAsClient(() => facts = ReadThreadToken());
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (InvalidOperationException) { return null; }
            return facts;
        }

        /// <summary>
        /// Whether PadForge takes the client: the desktop's user or the
        /// user PadForge runs as, in PadForge's own Windows session, at
        /// medium integrity or above, and not an app container. Null when it
        /// is taken, or the reason in a word when it is not.
        /// </summary>
        internal static string Judge(AutomationClientToken? token, string desktopUser, string serverUser, int serverSession)
        {
            if (token is not { } t || string.IsNullOrEmpty(t.UserSid)) return "no-token";
            bool sameUser = string.Equals(t.UserSid, desktopUser, StringComparison.OrdinalIgnoreCase)
                || string.Equals(t.UserSid, serverUser, StringComparison.OrdinalIgnoreCase);
            if (!sameUser) return "other-user";
            if (t.SessionId != serverSession) return "other-session";
            if (t.Integrity < AutomationClientToken.MediumIntegrity) return "low-integrity";
            if (t.AppContainer) return "app-container";
            return null;
        }

        /// <summary>The desktop's user as the control pipe's access list
        /// names it, and this process's user.</summary>
        internal static (string Desktop, string Server, int Session) ThisDesktop()
        {
            using var identity = WindowsIdentity.GetCurrent();
            string server = identity.User?.Value;
            string desktop = PadForge.Common.Input.Peripherals.WindowsLightingState.ShellUserSid();
            int session;
            using (var self = System.Diagnostics.Process.GetCurrentProcess()) session = self.SessionId;
            return (string.IsNullOrEmpty(desktop) ? server : desktop, server, session);
        }

        private static AutomationClientToken? ReadThreadToken()
        {
            if (!OpenThreadToken(GetCurrentThread(), TokenQuery, true, out var token) || token.IsInvalid)
            {
                token?.Dispose();
                return null;
            }
            using (token)
            {
                string sid = ReadUser(token);
                if (sid == null) return null;
                int session = ReadInt(token, TokenSessionId) ?? -1;
                int integrity = ReadIntegrity(token) ?? 0;
                bool container = (ReadInt(token, TokenIsAppContainer) ?? 0) != 0;
                return new AutomationClientToken(sid, session, integrity, container);
            }
        }

        private static string ReadUser(SafeAccessTokenHandle token)
        {
            GetTokenInformation(token, TokenUser, IntPtr.Zero, 0, out int size);
            if (size <= 0) return null;
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!GetTokenInformation(token, TokenUser, buffer, size, out _)) return null;
                IntPtr sid = Marshal.ReadIntPtr(buffer);
                return new SecurityIdentifier(sid).Value;
            }
            catch (ArgumentException) { return null; }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        private static int? ReadInt(SafeAccessTokenHandle token, int what)
        {
            IntPtr buffer = Marshal.AllocHGlobal(4);
            try
            {
                return GetTokenInformation(token, what, buffer, 4, out _) ? Marshal.ReadInt32(buffer) : null;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        /// <summary>The integrity level's RID: the last sub-authority of the
        /// label SID in TOKEN_MANDATORY_LABEL.</summary>
        private static int? ReadIntegrity(SafeAccessTokenHandle token)
        {
            GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out int size);
            if (size <= 0) return null;
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, size, out _)) return null;
                IntPtr sid = Marshal.ReadIntPtr(buffer);
                IntPtr count = GetSidSubAuthorityCount(sid);
                if (count == IntPtr.Zero) return null;
                int last = Marshal.ReadByte(count) - 1;
                if (last < 0) return null;
                return Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)last));
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        private const uint TokenQuery = 0x0008;
        private const int TokenUser = 1;
        private const int TokenSessionId = 12;
        private const int TokenIntegrityLevel = 25;
        private const int TokenIsAppContainer = 29;

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentThread();

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenThreadToken(IntPtr thread, uint access,
            [MarshalAs(UnmanagedType.Bool)] bool openAsSelf, out SafeAccessTokenHandle token);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int infoClass,
            IntPtr info, int length, out int returned);

        [DllImport("advapi32.dll")]
        private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

        [DllImport("advapi32.dll")]
        private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);
    }

    /// <summary>
    /// The process at the other end of a connection, held open for as long
    /// as the connection lasts, so a process ID Windows hands to a new
    /// program after this one exits is never shown as the program that
    /// connected (GameModSender's pattern). The ID and the image name are
    /// for the user's eyes and decide nothing.
    /// </summary>
    internal sealed class AutomationClientProcess : IDisposable
    {
        private readonly SafeProcessHandle _handle;
        private int _disposed;

        private AutomationClientProcess(int id, string imageName, SafeProcessHandle handle)
        {
            Id = id;
            ImageName = imageName;
            _handle = handle;
        }

        /// <summary>The process ID as Windows names it, or 0.</summary>
        public int Id { get; }

        /// <summary>The image's file name with no folder, or null.</summary>
        public string ImageName { get; }

        /// <summary>A client nothing is known of.</summary>
        internal static AutomationClientProcess None => new AutomationClientProcess(0, null, null);

        /// <summary>Opens the pipe's client process to hold it.</summary>
        internal static AutomationClientProcess Of(NamedPipeServerStream server)
        {
            uint pid = 0;
            try
            {
                if (!GetNamedPipeClientProcessId(server.SafePipeHandle, out pid)) pid = 0;
            }
            catch (ObjectDisposedException) { pid = 0; }
            catch (InvalidOperationException) { pid = 0; }
            return Of(unchecked((int)pid));
        }

        /// <summary>Opens process <paramref name="pid"/> to hold it. A
        /// process that cannot be opened is shown by its ID alone.</summary>
        internal static AutomationClientProcess Of(int pid)
        {
            if (pid <= 0) return None;
            var handle = OpenProcess(Synchronize | ProcessQueryLimitedInformation, false, pid);
            if (handle == null || handle.IsInvalid)
            {
                handle?.Dispose();
                return new AutomationClientProcess(pid, null, null);
            }
            return new AutomationClientProcess(pid, ReadName(handle), handle);
        }

        /// <summary>Whether a window belongs to this process: a script's
        /// window handle is taken only then, and checked again before each
        /// wake message.</summary>
        internal bool OwnsWindow(IntPtr hwnd)
        {
            if (Id <= 0 || hwnd == IntPtr.Zero) return false;
            GetWindowThreadProcessId(hwnd, out uint owner);
            return owner == (uint)Id && !HasExited;
        }

        /// <summary>Whether the process ended. One with no handle reads as
        /// running: the pipe closing says when it ended.</summary>
        internal bool HasExited
        {
            get
            {
                if (_handle == null || Volatile.Read(ref _disposed) != 0) return false;
                try { return WaitForSingleObject(_handle, 0) == 0; }
                catch (ObjectDisposedException) { return false; }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _handle?.Dispose();
        }

        private static string ReadName(SafeProcessHandle process)
        {
            var text = new StringBuilder(1024);
            int length = text.Capacity;
            if (!QueryFullProcessImageName(process, 0, text, ref length)) return null;
            try { return Path.GetFileName(text.ToString(0, length)); }
            catch (ArgumentException) { return null; }
        }

        private const int Synchronize = 0x00100000;
        private const int ProcessQueryLimitedInformation = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeProcessHandle OpenProcess(int access, bool inherit, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags,
            StringBuilder name, ref int size);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    }
}
