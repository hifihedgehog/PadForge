using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using PadForge.Common;

namespace PadForge.Services
{
    /// <summary>
    /// The folder PadForge.exe runs from (#597). Windows loads a library from
    /// an exe's own folder before System32's for any name that is not a known
    /// DLL, the runtime looks there for every <c>DllImport</c>, PadForge.xml
    /// sits there, and a task that runs PadForge with rights or as SYSTEM runs
    /// the exe that is there. A folder any account can write gives that
    /// account PadForge's rights: on this PC C:\PadForge is owned by its user
    /// and C:\ hands Authenticated Users modify over every folder made in it.
    ///
    /// <para>A folder that holds nothing but PadForge's own files
    /// (<see cref="IsPadForgeFile"/>) is PadForge's own, and a start with
    /// rights gives it the protected folder's list
    /// (<see cref="ProtectedFolder.Sddl"/>): owner Administrators,
    /// Administrators and SYSTEM full control, Users read and execute, which
    /// every file in it inherits. PadForge with rights still writes all of
    /// its files there. A folder that holds anything else, such as Downloads,
    /// is shared: PadForge changes nothing in it, and no task that runs with
    /// rights or as SYSTEM may run an exe there. Move to Its Own Folder copies
    /// PadForge to <c>%ProgramFiles%\PadForge</c>.</para>
    /// </summary>
    internal static class ExeFolder
    {
        /// <summary>The name of the folder PadForge moves into, under
        /// Program Files.</summary>
        internal const string OwnFolderName = "PadForge";

        internal const string ExeName = "PadForge.exe";

        /// <summary>The folder of game pictures PadForge keeps beside itself
        /// (GameLookStore.FolderName).</summary>
        internal const string GameLooksFolder = "GameLooks";

        /// <summary>Set by the test assembly for the life of its process,
        /// never by the app. No folder's list is changed, no task is looked
        /// at, and nothing is copied while it is set.</summary>
        internal static bool OffLimitsForTest;

        // ── The rule ──

        /// <summary>
        /// Whether a file of this name is one PadForge itself keeps beside the
        /// exe: the exe, PadForge.xml or Settings.xml and the files the
        /// settings code writes beside it (.tmp, .unreadable-*), crash.log,
        /// diagnostics.log and its .old, diag.log from before v4,
        /// diag-snapshot-*.log, PadForge-recording-*.zip and its .tmp and
        /// .log.partial, the first-run marker, and the sound, icon, theme and
        /// profile files imports land beside it, with their .tmp.
        /// </summary>
        internal static bool IsPadForgeFile(string name, string exeName)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.ToLowerInvariant();
            if (n == (exeName ?? ExeName).ToLowerInvariant() || n == ExeName.ToLowerInvariant()) return true;
            foreach (string settings in new[] { SettingsService.PrimaryFileName, SettingsService.FallbackFileName })
            {
                string s = settings.ToLowerInvariant();
                if (n == s || n == s + ".tmp" || n.StartsWith(s + ".unreadable-", StringComparison.Ordinal)) return true;
            }
            if (n is "crash.log" or "diagnostics.log" or "diagnostics.log.old" or "diag.log" or "padforge.firstrun") return true;
            if (n.StartsWith("diag-snapshot-", StringComparison.Ordinal) && n.EndsWith(".log", StringComparison.Ordinal)) return true;
            string recording = ProblemRecorder.FilePrefix.ToLowerInvariant();
            if (n.StartsWith(recording, StringComparison.Ordinal)
                && (n.EndsWith(".zip", StringComparison.Ordinal) || n.EndsWith(".zip.tmp", StringComparison.Ordinal)
                    || n.EndsWith(ProblemRecorder.PartialSuffix, StringComparison.Ordinal)))
                return true;
            foreach (string extension in new[]
                     {
                         SoundPackageManager.FileExtension, IconPackageManager.FileExtension,
                         PadForge.Common.Themes.ThemeFile.Extension, ProfileTransfer.FileExtension,
                     })
            {
                string e = extension.ToLowerInvariant();
                if (n.EndsWith(e, StringComparison.Ordinal) || n.EndsWith(e + ".tmp", StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>One entry of a folder as the rule reads it.</summary>
        internal sealed record Item(string Name, bool IsFolder, bool IsLink, int Links, IReadOnlyList<Item> Children);

        /// <summary>
        /// Whether a folder holding <paramref name="items"/> is PadForge's own:
        /// every file is one of PadForge's, none is a link or has a second
        /// name (a hard link), and the one folder allowed is GameLooks, which
        /// holds plain files alone.
        /// </summary>
        internal static bool IsOwn(IReadOnlyList<Item> items, string exeName)
        {
            if (items == null) return false;
            bool hasExe = false;
            foreach (var item in items)
            {
                if (item.IsLink || item.Links > 1) return false;
                if (item.IsFolder)
                {
                    if (!string.Equals(item.Name, GameLooksFolder, StringComparison.OrdinalIgnoreCase) || item.Children == null) return false;
                    foreach (var child in item.Children)
                        if (child.IsFolder || child.IsLink || child.Links > 1) return false;
                    continue;
                }
                if (!IsPadForgeFile(item.Name, exeName)) return false;
                if (string.Equals(item.Name, exeName, StringComparison.OrdinalIgnoreCase)) hasExe = true;
            }
            return hasExe;
        }

        /// <summary>A folder's entries, one level down and inside GameLooks.
        /// Null when it cannot be read.</summary>
        internal static IReadOnlyList<Item> Read(string folder)
        {
            try
            {
                var items = new List<Item>();
                foreach (var info in new DirectoryInfo(folder).EnumerateFileSystemInfos())
                {
                    bool link = (info.Attributes & FileAttributes.ReparsePoint) != 0;
                    bool isFolder = (info.Attributes & FileAttributes.Directory) != 0;
                    IReadOnlyList<Item> children = null;
                    if (isFolder && !link && string.Equals(info.Name, GameLooksFolder, StringComparison.OrdinalIgnoreCase))
                        children = Read(info.FullName);
                    items.Add(new Item(info.Name, isFolder, link, isFolder ? 1 : LinkCount(info.FullName), children));
                }
                return items;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>How many names a file has. More than one is a hard link,
        /// whose list is the other name's too.</summary>
        private static int LinkCount(string path)
        {
            try
            {
                using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return GetFileInformationByHandle(handle, out var info) ? (int)info.NumberOfLinks : int.MaxValue;
            }
            catch
            {
                // A file that cannot be opened to be counted is not one to
                // vouch for.
                return int.MaxValue;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public uint CreationTimeLow, CreationTimeHigh, LastAccessTimeLow, LastAccessTimeHigh, LastWriteTimeLow, LastWriteTimeHigh;
            public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, out ByHandleFileInformation info);

        // ── Who may change it ──

        /// <summary>
        /// Whether an account other than Administrators, SYSTEM and
        /// TrustedInstaller may change the folder or one of
        /// <paramref name="files"/>: write data, append, write attributes,
        /// delete, delete a child, change the list or take ownership, or own
        /// it. Anything that cannot be read counts as changeable.
        /// </summary>
        internal static bool OthersMayChange(string folder, IEnumerable<string> files)
        {
            if (Judge(folder, folder: true) != ProtectedFolder.Verdict.Sound) return true;
            foreach (string file in files)
                if (Judge(file, folder: false) != ProtectedFolder.Verdict.Sound) return true;
            return false;
        }

        private static ProtectedFolder.Verdict Judge(string path, bool folder)
        {
            byte[] descriptor = System32Calls.ReadDescriptor(path, out uint attributes);
            if (descriptor == null) return ProtectedFolder.Verdict.Unreadable;
            if ((attributes & System32Calls.AttributeReparsePoint) != 0) return ProtectedFolder.Verdict.NotAFolder;
            if (folder && (attributes & System32Calls.AttributeDirectory) == 0) return ProtectedFolder.Verdict.NotAFolder;
            return ProtectedFolder.Judge(descriptor, trustTrustedInstaller: true);
        }

        /// <summary>The files of an own folder, GameLooks' included, with
        /// their full paths.</summary>
        internal static List<string> FilesOf(string folder, IReadOnlyList<Item> items)
        {
            var files = new List<string>();
            foreach (var item in items)
            {
                string path = Path.Combine(folder, item.Name);
                if (!item.IsFolder)
                {
                    files.Add(path);
                    continue;
                }
                files.Add(path);
                foreach (var child in item.Children) files.Add(Path.Combine(path, child.Name));
            }
            return files;
        }

        // ── Protecting an own folder ──

        /// <summary>
        /// Gives an own folder the protected folder's list and owner, and has
        /// every file and folder in it take Administrators as owner and only
        /// what it inherits, so an entry a file was given on its own goes too.
        /// The folder holds no link and no hard link (<see cref="IsOwn"/>), so
        /// no list outside it changes. True when no other account may change
        /// any of it afterward.
        /// </summary>
        internal static bool Protect(string folder, IReadOnlyList<Item> items)
        {
            if (OffLimitsForTest) return false;
            try
            {
                using (Privileges.Hold("SeTakeOwnershipPrivilege", "SeRestorePrivilege"))
                {
                    var own = new DirectorySecurity();
                    own.SetSecurityDescriptorSddlForm(ProtectedFolder.Sddl);
                    new DirectoryInfo(folder).SetAccessControl(own);
                    var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
                    foreach (string path in FilesOf(folder, items))
                    {
                        if (Directory.Exists(path))
                        {
                            var inherit = new DirectorySecurity();
                            inherit.SetOwner(administrators);
                            inherit.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
                            new DirectoryInfo(path).SetAccessControl(inherit);
                        }
                        else
                        {
                            var inherit = new FileSecurity();
                            inherit.SetOwner(administrators);
                            inherit.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
                            new FileInfo(path).SetAccessControl(inherit);
                        }
                    }
                }
            }
            catch
            {
                return false;
            }
            return !OthersMayChange(folder, FilesOf(folder, items));
        }

        // ── A start with rights ──

        internal enum Standing
        {
            /// <summary>PadForge's own folder, and no other account may
            /// change it.</summary>
            Own,

            /// <summary>The folder holds other files, so PadForge leaves it as
            /// it is and runs no task from it.</summary>
            Shared,

            /// <summary>PadForge's own folder, but its list could not be made
            /// sound.</summary>
            Unprotected,
        }

        /// <summary>
        /// What the folder of <paramref name="exe"/> is, after an own folder
        /// other accounts could change has been protected. A start with rights
        /// asks this at once, before anything trusts the folder.
        /// </summary>
        internal static Standing Check(string exe)
        {
            string folder = Path.GetDirectoryName(exe);
            string name = Path.GetFileName(exe);
            var items = folder == null ? null : Read(folder);
            if (items == null || !IsOwn(items, name)) return Standing.Shared;
            if (!OthersMayChange(folder, FilesOf(folder, items))) return Standing.Own;
            return Protect(folder, items) ? Standing.Own : Standing.Unprotected;
        }

        /// <summary>Whether a task that runs with rights or as SYSTEM may run
        /// <paramref name="exe"/>: its folder is PadForge's own and no other
        /// account may change it. Nothing is changed by asking.</summary>
        internal static bool MayHoldTasks(string exe)
        {
            if (string.IsNullOrEmpty(exe)) return false;
            string folder = Path.GetDirectoryName(exe);
            var items = folder == null ? null : Read(folder);
            return items != null && IsOwn(items, Path.GetFileName(exe)) && !OthersMayChange(folder, FilesOf(folder, items));
        }

        // ── Move to Its Own Folder ──

        /// <summary>%ProgramFiles%\PadForge.</summary>
        internal static string OwnFolderPath()
        {
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            return string.IsNullOrEmpty(programFiles) ? null : Path.Combine(programFiles, OwnFolderName);
        }

        /// <summary>What a move came to: the new exe, or why not.
        /// <paramref name="TargetTaken"/> says the folder in Program Files holds
        /// what is not PadForge's, and <paramref name="Error"/> is then its
        /// path. Otherwise it is Windows' message.</summary>
        internal sealed record MoveResult(string NewExe, string Error, bool TargetTaken = false);

        /// <summary>
        /// Copies this exe and its settings file into <paramref name="target"/>:
        /// made with the protected list when it is not there, used when it is
        /// PadForge's own and can be made sound, refused when it holds anything
        /// else. The exe's copy is checked against the original's SHA-256
        /// before it takes the exe's name. The old exe is left where it was.
        /// </summary>
        internal static MoveResult CopyTo(string target, string exe, string settingsFile)
        {
            if (OffLimitsForTest) return new MoveResult(null, "test host");
            if (string.IsNullOrEmpty(target)) return new MoveResult(null, "no Program Files folder");
            try
            {
                if (!Directory.Exists(target))
                {
                    if (!System32Calls.CreateFolder(target, ProtectedFolder.Sddl) && !Directory.Exists(target))
                        return new MoveResult(null, target, TargetTaken: true);
                }
                var items = Read(target);
                bool empty = items != null && items.Count == 0;
                if (items == null || (!empty && !IsOwnOrEmpty(items))) return new MoveResult(null, target, TargetTaken: true);
                if (OthersMayChange(target, FilesOf(target, items)) && !Protect(target, items)) return new MoveResult(null, target, TargetTaken: true);

                string newExe = Path.Combine(target, ExeName);
                string staged = newExe + ".tmp";
                File.Copy(exe, staged, overwrite: true);
                if (!string.Equals(HashOf(staged), HashOf(exe), StringComparison.Ordinal))
                {
                    File.Delete(staged);
                    return new MoveResult(null, target, TargetTaken: true);
                }
                File.Move(staged, newExe, overwrite: true);
                if (!string.IsNullOrEmpty(settingsFile) && File.Exists(settingsFile))
                    File.Copy(settingsFile, Path.Combine(target, Path.GetFileName(settingsFile)), overwrite: true);
                return MayHoldTasks(newExe) ? new MoveResult(newExe, null) : new MoveResult(null, target, TargetTaken: true);
            }
            catch (Exception ex)
            {
                return new MoveResult(null, ex.Message);
            }
        }

        /// <summary>An own folder may hold no exe yet only while it is being
        /// filled: PadForge's other files alone.</summary>
        private static bool IsOwnOrEmpty(IReadOnlyList<Item> items)
        {
            foreach (var item in items)
            {
                if (item.IsLink || item.Links > 1) return false;
                if (item.IsFolder)
                {
                    if (!string.Equals(item.Name, GameLooksFolder, StringComparison.OrdinalIgnoreCase) || item.Children == null) return false;
                    foreach (var child in item.Children)
                        if (child.IsFolder || child.IsLink || child.Links > 1) return false;
                    continue;
                }
                if (!IsPadForgeFile(item.Name, ExeName)) return false;
            }
            return true;
        }

        private static string HashOf(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Convert.ToHexString(SHA256.HashData(stream));
        }

        /// <summary>Holds token privileges enabled for as long as it lives,
        /// as Windows' own tools do to set an owner they do not hold
        /// (Microsoft, Enabling and Disabling Privileges).</summary>
        private sealed class Privileges : IDisposable
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
}
