using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using PadForge.Services.Install;
using PadForge.Services.Plugins;

namespace PadForge.Common
{
    /// <summary>
    /// The folders and the registry hive of the user whose desktop this is.
    ///
    /// <para>PadForge runs with rights. When a standard user types an
    /// administrator's credentials at its rights prompt, this process's
    /// %APPDATA%, %LOCALAPPDATA% and HKEY_CURRENT_USER are the
    /// administrator's, and another program's files and settings for the
    /// user at the desktop are elsewhere. A folder is read through the
    /// known-folder API with the token of the shell's process
    /// (SHGetKnownFolderPath, as RecordingFolder.Downloads reads the
    /// Downloads folder), and a key under that user's SID in HKEY_USERS (as
    /// WindowsLightingState reads the lighting switches). Where this process
    /// has no rights the shell lacks, or there is no shell, the process's
    /// own are read: they are then the same user's.</para>
    /// </summary>
    internal static class ShellUser
    {
        // KnownFolders.h.
        private static readonly Guid FolderIdProfile = new("5E6C858F-0E22-4760-9AFE-EA3317B67173");
        private static readonly Guid FolderIdDocuments = new("FDD39AD0-238F-46AF-ADB4-6C85480369C7");
        private static readonly Guid FolderIdRoamingAppData = new("3EB685DB-65F9-4CF6-A03A-E3EF65729F3D");
        private static readonly Guid FolderIdLocalAppData = new("F1B32785-6FBA-4FCF-9D55-7B8E7F157091");

        private const uint TokenQuery = 0x0008, TokenImpersonate = 0x0004, TokenDuplicate = 0x0002;
        private const int SecurityImpersonation = 2, TokenImpersonation = 2;

        /// <summary>Runs <paramref name="work"/> as the user on this thread:
        /// in this process as it is when it has no rights the desktop shell
        /// lacks, and otherwise under the shell's token, the token a program
        /// PadForge starts for the user runs with (PluginLauncher). False,
        /// with nothing run, when no shell runs or its token cannot be
        /// read. Used for work in folders and files the user may change: the
        /// temp folder's leftovers (TempLeftovers) and SteamVR's path
        /// registry (DriverInstaller.ContainOwnedSteamVrDataPaths).</summary>
        internal static bool AsTheUser(Action work)
        {
            if (PluginLauncher.ThisProcessHasAdministratorRights() == false)
            {
                work();
                return true;
            }
            using var shell = PluginLauncher.OpenShell();
            if (shell == null) return false;
            bool? shellRights = PluginLauncher.HasAdministratorRights(shell);
            if (shellRights == null) return false;
            if (shellRights == true)
            {
                // UAC is off: every program of the user has these rights.
                work();
                return true;
            }
            if (!OpenProcessToken(shell, TokenQuery | TokenDuplicate, out var token)) return false;
            using (token)
            {
                if (!DuplicateTokenEx(token, TokenQuery | TokenImpersonate, IntPtr.Zero, SecurityImpersonation, TokenImpersonation, out var user))
                    return false;
                using (user) System.Security.Principal.WindowsIdentity.RunImpersonated(user, work);
            }
            return true;
        }

        /// <summary>The folder of that user, or null when Windows names
        /// none.</summary>
        internal static string Folder(UserFolder folder)
        {
            Guid id = folder switch
            {
                UserFolder.Documents => FolderIdDocuments,
                UserFolder.RoamingData => FolderIdRoamingAppData,
                UserFolder.LocalData => FolderIdLocalAppData,
                _ => FolderIdProfile,
            };
            try
            {
                if (PluginLauncher.ThisProcessHasAdministratorRights() != false)
                {
                    using var shell = PluginLauncher.OpenShell();
                    if (shell != null && PluginLauncher.HasAdministratorRights(shell) == false)
                    {
                        if (!OpenProcessToken(shell, TokenQuery | TokenImpersonate | TokenDuplicate, out var token)) return null;
                        using (token) return KnownFolder(id, token.DangerousGetHandle());
                    }
                }
                return KnownFolder(id, IntPtr.Zero);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ExternalException)
            {
                return null;
            }
        }

        /// <summary>A key of that user's hive, read only, or null when it is
        /// not there. The hive under the shell user's SID in HKEY_USERS
        /// answers where it is loaded, and this process's own
        /// HKEY_CURRENT_USER where there is no shell or no hive of its
        /// user.</summary>
        internal static RegistryKey OpenKey(string subKey)
        {
            string sid = PadForge.Common.Input.Peripherals.WindowsLightingState.ShellUserSid();
            if (sid != null)
            {
                using var hive = Registry.Users.OpenSubKey(sid);
                if (hive != null) return hive.OpenSubKey(subKey);
            }
            return Registry.CurrentUser.OpenSubKey(subKey);
        }

        private static string KnownFolder(Guid id, IntPtr token)
        {
            if (SHGetKnownFolderPath(id, 0, token, out IntPtr text) != 0) return null;
            try { return Marshal.PtrToStringUni(text); }
            finally { Marshal.FreeCoTaskMem(text); }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHGetKnownFolderPath(in Guid rfid, uint flags, IntPtr token, out IntPtr path);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool DuplicateTokenEx(SafeAccessTokenHandle existing, uint access, IntPtr attributes,
            int impersonationLevel, int tokenType, out SafeAccessTokenHandle token);
    }
}
