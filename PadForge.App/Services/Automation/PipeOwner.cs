// Who serves a PadForge pipe (#600), from the PadForge Plugin Kit. PadForge
// builds this file, and the kit carries the same file for its C# plugin, so
// the kit's plugin, the client run, the copy an assistant starts and the
// script libraries keep one rule.
//
// To build the kit's C# plugin, put PadForgePlugin.cs in place of a console
// program's Program.cs and this file beside it, as PadForgePlugin.cs says,
// then add the program on PadForge's Plugins page with Add a Plugin….

#nullable enable

using System;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace PadForge.Services.Automation
{
    /// <summary>
    /// Whether the server at the other end of a pipe is PadForge (#600).
    /// PadForge serves its pipes with administrator rights, and Windows makes
    /// the Administrators group the owner of a pipe such a process creates
    /// (PadForge's server also names that owner itself,
    /// ExternalControlService.NameOwner). A process without rights cannot
    /// give its pipe that owner, nor the system's: in its token the group is
    /// there for denying only. So a client reads the owner of the pipe it
    /// connected to (GetSecurityInfo, OWNER_SECURITY_INFORMATION, on its own
    /// handle, which the pipe's access list opens for reading its security)
    /// and sends nothing to a pipe another program made under PadForge's
    /// name.
    ///
    /// <para>The one rule: a pipe of one of PadForge's own names
    /// (<see cref="OwnNames"/>, compared without regard to case, as Windows
    /// compares pipe names) is taken only with Administrators or the system
    /// as its owner. A pipe of any other name, which only a test serves, is
    /// taken from the caller's own user too.</para>
    ///
    /// <para>A client opens the pipe at the identification level
    /// (SECURITY_SQOS_PRESENT with SECURITY_IDENTIFICATION, and
    /// TokenImpersonationLevel.Identification in .NET), so a server that is
    /// not PadForge learns who connected and cannot act as that user.
    /// PadForge itself only reads the client's token, which that level
    /// allows.</para>
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static partial class PipeOwner
    {
        /// <summary>The owners a PadForge pipe has: Administrators and the
        /// system.</summary>
        internal const string Administrators = "S-1-5-32-544", LocalSystem = "S-1-5-18";

        /// <summary>The names of the pipes PadForge serves: the control pipe,
        /// the Automation pipe and the signal pipe a second start asks the
        /// running copy over.</summary>
        internal static readonly string[] OwnNames = { "PadForge.Control", "PadForge.Automation", "PadForge.Instance" };

        /// <summary>Whether <paramref name="pipeName"/> is one of PadForge's
        /// own, without regard to case.</summary>
        internal static bool IsOwnName(string? pipeName)
        {
            foreach (string name in OwnNames)
                if (string.Equals(name, pipeName, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>Whether the pipe behind <paramref name="handle"/>, opened
        /// under <paramref name="pipeName"/>, is served by PadForge, by the
        /// one rule.</summary>
        internal static bool IsServer(SafePipeHandle handle, string pipeName)
            => Taken(Of(handle), IsOwnName(pipeName) ? null : OwnUser);

        /// <summary>The rule alone, for the tests that stand an owner in:
        /// Administrators, the system, or <paramref name="alsoTaken"/> when
        /// one is given.</summary>
        internal static bool Taken(string? owner, string? alsoTaken = null)
            => owner != null && (owner == Administrators || owner == LocalSystem
                || (!string.IsNullOrEmpty(alsoTaken) && string.Equals(owner, alsoTaken, StringComparison.OrdinalIgnoreCase)));

        /// <summary>The owner of the pipe behind <paramref name="handle"/>
        /// as a SID string, or null when it cannot be read.</summary>
        internal static string? Of(SafePipeHandle handle)
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

        /// <summary>The user this process runs as, as a SID string.</summary>
        internal static readonly string? OwnUser = ReadOwnUser();

        private static string? ReadOwnUser()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return identity.User?.Value;
            }
            catch (System.Security.SecurityException) { return null; }
        }

        private const int SE_KERNEL_OBJECT = 6;
        private const uint OWNER_SECURITY_INFORMATION = 0x00000001;

        [DllImport("advapi32.dll", SetLastError = false)]
        private static extern int GetSecurityInfo(SafePipeHandle handle, int objectType, uint securityInfo,
            out IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl, out IntPtr securityDescriptor);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);
    }
}
