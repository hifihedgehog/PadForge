using System;
using System.Runtime.InteropServices;

namespace PadForge.Common
{
    /// <summary>Holds token privileges enabled for as long as it lives,
    /// as Windows' own tools do to set an owner they do not hold
    /// (Microsoft, Enabling and Disabling Privileges), and gives back the
    /// state they had when it ends: ExeFolder's protection of PadForge's
    /// folder, and the logon start's launch as SYSTEM.</summary>
    internal sealed class Privileges : IDisposable
    {
        private readonly IntPtr _token;
        private readonly byte[] _previous;

        private Privileges(IntPtr token, byte[] previous)
        {
            _token = token;
            _previous = previous;
        }

        internal static Privileges Hold(params string[] names)
        {
            const uint TokenAdjustPrivileges = 0x20, TokenQuery = 0x8;
            if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out IntPtr token)) return new Privileges(IntPtr.Zero, null);
            int size = 4 + names.Length * 12;
            byte[] state = new byte[size];
            BitConverter.GetBytes(names.Length).CopyTo(state, 0);
            for (int i = 0; i < names.Length; i++)
            {
                if (!LookupPrivilegeValue(null, names[i], out long luid)) continue;
                BitConverter.GetBytes(luid).CopyTo(state, 4 + i * 12);
                BitConverter.GetBytes(2u).CopyTo(state, 4 + i * 12 + 8); // SE_PRIVILEGE_ENABLED
            }
            byte[] previous = new byte[size];
            AdjustTokenPrivileges(token, false, state, (uint)size, previous, out _);
            return new Privileges(token, previous);
        }

        public void Dispose()
        {
            if (_token == IntPtr.Zero) return;
            if (_previous != null) AdjustTokenPrivileges(_token, false, _previous, (uint)_previous.Length, null, out _);
            CloseHandle(_token);
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "LookupPrivilegeValueW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool LookupPrivilegeValue(string system, string name, out long luid);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, byte[] state, uint length, byte[] previous, out uint returned);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
