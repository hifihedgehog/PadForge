using System;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace PadForge.Services.Automation
{
    /// <summary>
    /// Whether the server at the other end of a pipe is PadForge (#597).
    /// PadForge serves its pipes with administrator rights, and Windows makes
    /// the Administrators group the owner of a pipe such a process creates
    /// (the server also names that owner itself,
    /// ExternalControlService.BuildSecurity). A process without rights cannot
    /// give its pipe that owner, nor the system's: in its token the group is
    /// there for denying only. So a client reads the owner of the pipe it
    /// connected to (GetSecurityInfo, OWNER_SECURITY_INFORMATION, on its own
    /// handle, which the pipe's access list opens for reading its security)
    /// and sends nothing to a pipe another program made under PadForge's
    /// name.
    ///
    /// <para>A client opens the pipe at the identification level
    /// (SECURITY_SQOS_PRESENT with SECURITY_IDENTIFICATION, and
    /// TokenImpersonationLevel.Identification in .NET), so a server that is
    /// not PadForge learns who connected and cannot act as that user. PadForge
    /// itself only reads the client's token, which that level
    /// allows.</para>
    /// </summary>
    internal static class PipeOwner
    {
        /// <summary>The owners a PadForge pipe has: Administrators and the
        /// system.</summary>
        internal const string Administrators = "S-1-5-32-544", LocalSystem = "S-1-5-18";

        /// <summary>The owner of the pipe behind <paramref name="handle"/>
        /// as a SID string, or null when it cannot be read.</summary>
        internal static string Of(SafePipeHandle handle)
        {
            if (handle == null || handle.IsInvalid || handle.IsClosed) return null;
            IntPtr descriptor = IntPtr.Zero;
            try
            {
                int error = GetSecurityInfo(handle, SE_KERNEL_OBJECT, OWNER_SECURITY_INFORMATION,
                    out IntPtr owner, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out descriptor);
                if (error != 0 || owner == IntPtr.Zero) return null;
                return new SecurityIdentifier(owner).Value;
            }
            catch (ArgumentException)
            {
                return null;
            }
            finally
            {
                if (descriptor != IntPtr.Zero) LocalFree(descriptor);
            }
        }

        /// <summary>Whether the pipe's owner is one PadForge's pipes have.</summary>
        internal static bool IsPadForges(SafePipeHandle handle) => Taken(Of(handle));

        /// <summary>The one rule every client keeps: a pipe of PadForge's own
        /// name (<paramref name="padForgesName"/>) is taken only with an
        /// owner PadForge's pipes have. A pipe of another name, which a test
        /// serves, is taken from the client's own user too, as the plugin
        /// kit and the script libraries take it.</summary>
        internal static bool IsServer(SafePipeHandle handle, bool padForgesName)
            => Taken(Of(handle), padForgesName ? null : OwnUser);

        /// <summary>The user this process runs as, as a SID string.</summary>
        internal static readonly string OwnUser = ReadOwnUser();

        private static string ReadOwnUser()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return identity.User?.Value;
            }
            catch (System.Security.SecurityException) { return null; }
        }

        /// <summary>The rule alone, for the tests that stand an owner
        /// in.</summary>
        internal static bool Taken(string owner, string alsoTaken = null)
            => owner != null && (owner == Administrators || owner == LocalSystem
                || (!string.IsNullOrEmpty(alsoTaken) && string.Equals(owner, alsoTaken, StringComparison.OrdinalIgnoreCase)));

        private const int SE_KERNEL_OBJECT = 6;
        private const uint OWNER_SECURITY_INFORMATION = 0x00000001;

        [DllImport("advapi32.dll", SetLastError = false)]
        private static extern int GetSecurityInfo(SafePipeHandle handle, int objectType, uint securityInfo,
            out IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl, out IntPtr securityDescriptor);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);
    }
}
