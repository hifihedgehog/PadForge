using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using PadForge.Common;

namespace PadForge.Services
{
    /// <summary>
    /// The folder PadForge.exe runs from (#600). Windows loads a library from
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
        /// diagnostics.log and its .old, diag.log (the mirror the trace tools
        /// arm through PADFORGE_DIAG, and the name builds before v4 wrote) and
        /// its .old,
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
            if (n is "crash.log" or "diagnostics.log" or "diagnostics.log.old" or "diag.log" or "diag.log.old" or "padforge.firstrun") return true;
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

        /// <summary>One entry of a folder as the rule reads it, with its file
        /// ID on its volume, which the protection checks the entry by.</summary>
        internal sealed record Item(string Name, bool IsFolder, bool IsLink, int Links, IReadOnlyList<Item> Children, ulong Id = 0);

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
                    var (links, id) = Identity(info.FullName);
                    items.Add(new Item(info.Name, isFolder, link, isFolder ? 1 : links, children, id));
                }
                return items;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>How many names an entry has, and its file ID, read from
        /// the entry itself, a link as the link. More than one name is a hard
        /// link, whose list is the other name's too. An entry that cannot be
        /// opened to be read is not one to vouch for.</summary>
        private static (int Links, ulong Id) Identity(string path)
        {
            using var handle = HandleTree.OpenByPath(path, ReadAttributes);
            return handle != null && HandleTree.Identify(handle, out _, out _, out ulong id, out uint links)
                ? ((int)Math.Min(links, int.MaxValue), id)
                : (int.MaxValue, 0UL);
        }

        private const uint ReadAttributes = 0x00000080;

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
        /// Each entry is set through a handle opened relative to its folder's
        /// own handle, as itself and never through a link
        /// (<see cref="ProtectEntries"/>), and only when it is still the entry
        /// <see cref="Read"/> saw: the folder is still one other accounts may
        /// change while this runs, and a link swapped in after the reading
        /// would take a list set by path to what it names. True when no other
        /// account may change any of it afterward.
        /// </summary>
        internal static bool Protect(string folder, IReadOnlyList<Item> items)
        {
            if (OffLimitsForTest) return false;
            return ProtectAt(folder, items);
        }

        /// <summary>Test seam: called once the folder's own list is set and
        /// before its entries are. Null in the app.</summary>
        internal static Action<string> BeforeEntriesSeam;

        /// <summary>The protection's own steps, for a folder of a test's own
        /// too. It sets owners, so it needs administrator rights.</summary>
        internal static bool ProtectAt(string folder, IReadOnlyList<Item> items)
        {
            try
            {
                using (Privileges.Hold("SeTakeOwnershipPrivilege", "SeRestorePrivilege"))
                {
                    using var root = HandleTree.OpenByPath(folder, SecurityAccess | HandleTree.ListAccess);
                    if (root == null || !HandleTree.Identify(root, out uint attributes, out _, out _, out _)
                        || (attributes & HandleTree.AttributeReparsePoint) != 0 || (attributes & HandleTree.AttributeDirectory) == 0)
                        return false;
                    if (!SetSecurity(root, ProtectedFolder.Sddl, protect: true)) return false;
                    BeforeEntriesSeam?.Invoke(folder);
                    if (!ProtectEntries(root, items, SetInherited)) return false;
                }
            }
            catch
            {
                return false;
            }
            return !OthersMayChange(folder, FilesOf(folder, items));
        }

        /// <summary>
        /// Gives each of <paramref name="items"/>, opened relative to
        /// <paramref name="folder"/>'s handle as itself, the list it inherits,
        /// through <paramref name="setInherited"/> (given the entry's handle
        /// and whether it is a folder), and GameLooks' files after GameLooks.
        /// False at the first entry that cannot be opened, is a link now, has
        /// a second name, or is not the file <see cref="Read"/> saw by its ID.
        /// </summary>
        internal static bool ProtectEntries(SafeFileHandle folder, IReadOnlyList<Item> items, Func<SafeFileHandle, bool, bool> setInherited)
        {
            foreach (var item in items ?? Array.Empty<Item>())
            {
                using var entry = HandleTree.OpenRelative(folder, item.Name, SecurityAccess | (item.IsFolder ? HandleTree.ListAccess : 0));
                if (entry == null || !HandleTree.Identify(entry, out uint attributes, out _, out ulong id, out uint links)) return false;
                bool isFolder = (attributes & HandleTree.AttributeDirectory) != 0;
                if ((attributes & HandleTree.AttributeReparsePoint) != 0 || isFolder != item.IsFolder || id != item.Id
                    || (!isFolder && links > 1))
                    return false;
                if (!setInherited(entry, isFolder)) return false;
                if (isFolder && !ProtectEntries(entry, item.Children, setInherited)) return false;
            }
            return true;
        }

        /// <summary>The lists a file and a folder inherit in a folder of
        /// <see cref="ProtectedFolder.Sddl"/>, whose entries are all
        /// inherited by files and folders alike (OICI): owner
        /// Administrators.</summary>
        internal const string InheritedFileSddl = "O:BAD:AI(A;ID;FA;;;BA)(A;ID;FA;;;SY)(A;ID;0x1200a9;;;BU)";
        internal const string InheritedFolderSddl = "O:BAD:AI(A;OICIID;FA;;;BA)(A;OICIID;FA;;;SY)(A;OICIID;0x1200a9;;;BU)";

        private static bool SetInherited(SafeFileHandle entry, bool isFolder)
            => SetSecurity(entry, isFolder ? InheritedFolderSddl : InheritedFileSddl, protect: false);

        /// <summary>Sets the owner and list <paramref name="sddl"/> states on
        /// the object behind <paramref name="handle"/>, its list protected
        /// from inheritance or not.</summary>
        internal static bool SetSecurity(SafeFileHandle handle, string sddl, bool protect)
        {
            var descriptor = new RawSecurityDescriptor(sddl);
            var owner = new byte[descriptor.Owner.BinaryLength];
            descriptor.Owner.GetBinaryForm(owner, 0);
            var dacl = new byte[descriptor.DiscretionaryAcl.BinaryLength];
            descriptor.DiscretionaryAcl.GetBinaryForm(dacl, 0);
            uint information = OwnerSecurityInformation | DaclSecurityInformation
                | (protect ? ProtectedDaclSecurityInformation : UnprotectedDaclSecurityInformation);
            return SetSecurityInfo(handle, SeFileObject, information, owner, IntPtr.Zero, dacl, IntPtr.Zero) == 0;
        }

        private const uint SecurityAccess = 0x00020000 | 0x00040000 | 0x00080000 | 0x00000080; // READ_CONTROL | WRITE_DAC | WRITE_OWNER | FILE_READ_ATTRIBUTES
        private const int SeFileObject = 1;
        private const uint OwnerSecurityInformation = 0x00000001;
        private const uint DaclSecurityInformation = 0x00000004;
        private const uint ProtectedDaclSecurityInformation = 0x80000000;
        private const uint UnprotectedDaclSecurityInformation = 0x20000000;

        [DllImport("advapi32.dll")]
        private static extern int SetSecurityInfo(SafeFileHandle handle, int objectType, uint information, byte[] owner, IntPtr group,
            byte[] dacl, IntPtr sacl);

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

            /// <summary>The folder could not be listed, so nothing in it was
            /// read and PadForge leaves it as it is.</summary>
            Unread,
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
            if (items == null) return Standing.Unread;
            if (!IsOwn(items, name)) return Standing.Shared;
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

        /// <summary>Why a move did not happen.</summary>
        internal enum MoveRefusal
        {
            /// <summary>It happened.</summary>
            None,

            /// <summary>Windows named no Program Files folder.</summary>
            NoProgramFiles,

            /// <summary>This copy holds no handle on the exe that runs
            /// (<see cref="HoldRunningImage"/>), so the bytes that run cannot
            /// be read for certain.</summary>
            NoImage,

            /// <summary>The folder was not there and could not be
            /// made.</summary>
            NotMade,

            /// <summary>What the folder holds could not be read.</summary>
            Unreadable,

            /// <summary>The folder holds files that are not PadForge's.</summary>
            TargetTaken,

            /// <summary>Other accounts may change the folder, and it could not
            /// be protected.</summary>
            NotProtected,

            /// <summary>The copy did not match the exe that runs.</summary>
            CopyDiffers,

            /// <summary>After the copy, the folder held files that are not
            /// PadForge's, or other accounts could change it, so no task may
            /// run PadForge from it.</summary>
            NotSound,

            /// <summary>Windows refused a step, and the result's Error is its
            /// message.</summary>
            Failed,
        }

        /// <summary>What a move came to: the new exe, or why not.
        /// <paramref name="Error"/> is the folder's path for a refusal that
        /// names it, and Windows' message for <see cref="MoveRefusal.Failed"/>.</summary>
        internal sealed record MoveResult(string NewExe, string Error, MoveRefusal Why = MoveRefusal.Failed)
        {
            /// <summary>Whether the folder holds files that are not
            /// PadForge's.</summary>
            public bool TargetTaken => Why == MoveRefusal.TargetTaken;
        }

        /// <summary>
        /// Copies this exe and its settings file into <paramref name="target"/>:
        /// made with the protected list when it is not there, used when it is
        /// PadForge's own and can be made sound, refused when it holds anything
        /// else. The exe's bytes are read from the hold this copy took on the
        /// exe that runs at its start (<see cref="RunningImage"/>), never from
        /// its path again, and the copy is checked against their SHA-256
        /// before it takes the exe's name. The old exe is left where it was.
        /// </summary>
        internal static MoveResult CopyTo(string target, string exe, string settingsFile)
        {
            if (OffLimitsForTest) return new MoveResult(null, "test host");
            return CopyInto(target, exe, settingsFile, RunningImage);
        }

        /// <summary>The copy's own steps, for the folder named and the exe
        /// held in <paramref name="image"/>. The app gives Program Files'
        /// folder and its own hold (<see cref="CopyTo"/>), a test a folder of
        /// its own.</summary>
        internal static MoveResult CopyInto(string target, string exe, string settingsFile, SafeFileHandle image)
        {
            if (string.IsNullOrEmpty(target)) return new MoveResult(null, null, MoveRefusal.NoProgramFiles);
            try
            {
                if (!Directory.Exists(target))
                {
                    if (!System32Calls.CreateFolder(target, ProtectedFolder.Sddl) && !Directory.Exists(target))
                        return new MoveResult(null, target, MoveRefusal.NotMade);
                }
                var items = Read(target);
                if (items == null) return new MoveResult(null, target, MoveRefusal.Unreadable);
                if (items.Count > 0 && !IsOwnOrEmpty(items)) return new MoveResult(null, target, MoveRefusal.TargetTaken);
                if (OthersMayChange(target, FilesOf(target, items)) && !Protect(target, items)) return new MoveResult(null, target, MoveRefusal.NotProtected);
                if (image == null || image.IsInvalid || image.IsClosed) return new MoveResult(null, null, MoveRefusal.NoImage);

                string newExe = Path.Combine(target, ExeName);
                string staged = newExe + ".tmp";
                if (!CopyImage(image, exe, staged))
                {
                    File.Delete(staged);
                    return new MoveResult(null, target, MoveRefusal.CopyDiffers);
                }
                File.Move(staged, newExe, overwrite: true);
                if (!string.IsNullOrEmpty(settingsFile) && File.Exists(settingsFile))
                    File.Copy(settingsFile, Path.Combine(target, Path.GetFileName(settingsFile)), overwrite: true);
                return MayHoldTasks(newExe) ? new MoveResult(newExe, null, MoveRefusal.None) : new MoveResult(null, target, MoveRefusal.NotSound);
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

        /// <summary>Copies the exe held in <paramref name="image"/> to
        /// <paramref name="staged"/> and checks the copy against those bytes.
        /// The path <paramref name="exe"/> is not read: any program of the
        /// user can rename the running exe in a shared folder and put another
        /// file at its name. True when they match.</summary>
        internal static bool CopyImage(SafeFileHandle image, string exe, string staged)
        {
            string held;
            using (var copy = new FileStream(staged, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[81920];
                long at = 0;
                int n;
                while ((n = RandomAccess.Read(image, buffer, at)) > 0)
                {
                    copy.Write(buffer, 0, n);
                    hash.AppendData(buffer, 0, n);
                    at += n;
                }
                held = Convert.ToHexString(hash.GetHashAndReset());
            }
            return string.Equals(HashOf(staged), held, StringComparison.Ordinal);
        }

        private static string HashOf(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Convert.ToHexString(SHA256.HashData(stream));
        }

        // ── The exe that runs ──

        /// <summary>This copy's hold on the exe it runs as, taken at the
        /// start with rights (<see cref="HoldRunningImage"/>) and kept for
        /// the process's life. Null before, in the test host, and when it
        /// could not be taken.</summary>
        internal static SafeFileHandle RunningImage { get; private set; }

        /// <summary>Takes the hold on the exe this process runs as: opened by
        /// the name Windows gives its mapped image now, which follows a
        /// rename, with no sharing but reads, so the file can be neither
        /// renamed nor changed while PadForge runs. It counts only when the
        /// image's name is still the same once the hold is taken, so the file
        /// held is the image's.</summary>
        internal static void HoldRunningImage()
        {
            if (OffLimitsForTest || RunningImage != null) return;
            RunningImage = OpenImageOf(GetModuleHandleW(null));
        }

        /// <summary>The image of the module at <paramref name="module"/>,
        /// opened for reading with no sharing but reads. Null when it cannot
        /// be opened, or the image's name changed while it was.</summary>
        internal static SafeFileHandle OpenImageOf(IntPtr module)
        {
            string before = MappedName(module);
            if (before == null) return null;
            var handle = CreateFileW(@"\\?\GLOBALROOT" + before, GenericRead, FileShareRead, IntPtr.Zero, OpenExistingFile, 0, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                return null;
            }
            if (!string.Equals(MappedName(module), before, StringComparison.OrdinalIgnoreCase))
            {
                handle.Dispose();
                return null;
            }
            return handle;
        }

        /// <summary>The name of the file mapped at <paramref name="module"/>,
        /// in Windows' device form, as it is now. Null when it cannot be
        /// read.</summary>
        private static string MappedName(IntPtr module)
        {
            if (module == IntPtr.Zero) return null;
            var name = new char[32768];
            uint length = K32GetMappedFileNameW(GetCurrentProcess(), module, name, (uint)name.Length);
            return length == 0 ? null : new string(name, 0, (int)length);
        }

        private const uint GenericRead = 0x80000000;
        private const uint FileShareRead = 0x1;
        private const uint OpenExistingFile = 3;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandleW(string name);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint K32GetMappedFileNameW(IntPtr process, IntPtr address, [Out] char[] name, uint size);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    }
}
