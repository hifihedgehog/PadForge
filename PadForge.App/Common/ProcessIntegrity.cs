using System;
using System.Runtime.InteropServices;

namespace PadForge.Common
{
    /// <summary>A process's integrity level, read from its token. Uses no
    /// window toolkit, so a second copy of the exe reads it before WPF
    /// loads.</summary>
    internal static class ProcessIntegrity
    {
        /// <summary>The RIDs of the levels compared against: medium is a
        /// program without administrator rights, high one with them.</summary>
        internal const int Medium = 0x2000, High = 0x3000;

        /// <summary>A process's integrity level's RID: the last
        /// sub-authority of the label SID in its token, or null when the
        /// process or its token cannot be opened. A process without rights
        /// reads an elevated process of its own user this way: both opens
        /// ask only to query.</summary>
        internal static int? Of(uint pid)
        {
            if (pid == 0) return null;
            IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (process == IntPtr.Zero) return null;
            try
            {
                if (!OpenProcessToken(process, TOKEN_QUERY, out IntPtr token)) return null;
                try { return OfToken(token); }
                finally { CloseHandle(token); }
            }
            finally { CloseHandle(process); }
        }

        private static int? OfToken(IntPtr token)
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

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint TOKEN_QUERY = 0x0008;
        private const int TokenIntegrityLevel = 25;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returned);

        [DllImport("advapi32.dll")]
        private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

        [DllImport("advapi32.dll")]
        private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
