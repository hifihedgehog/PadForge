using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PadForge.Common
{
    /// <summary>
    /// A folder another account may change, opened and deleted by handles
    /// alone (#600). Directory.Delete with recursive lists a folder and opens
    /// each entry it listed again by its path (dotnet-runtime
    /// FileSystem.Windows.cs, RemoveDirectoryRecursive), so a subfolder
    /// swapped for a junction between the two takes a delete with rights into
    /// the junction's target. Here each entry is opened relative to its
    /// folder's held handle, as itself: a link is opened as the link and never
    /// as what it names (FILE_OPEN_REPARSE_POINT), and deleted through that
    /// handle (FileDispositionInfoEx). A folder is listed through its own
    /// handle. The walk goes at most <see cref="MostDepth"/> folders down and
    /// leaves what lies deeper.
    /// </summary>
    internal static class HandleTree
    {
        /// <summary>How many folders down a walk goes.</summary>
        internal const int MostDepth = 64;

        /// <summary>Test seam: called with a folder's path once its entries
        /// are listed, before any of them is opened. Null in the app.</summary>
        internal static Action<string> ListedSeam;

        /// <summary>Deletes <paramref name="path"/> and everything in it. A
        /// link is deleted as the link. True when all of it is gone.</summary>
        internal static bool Delete(string path)
        {
            // The test host deletes nothing outside the temp folder
            // (ProtectedFolder.OffLimitsForTest).
            if (ProtectedFolder.OffLimitsForTest && !UnderTemp(path)) return false;
            using var root = OpenByPath(path);
            return root != null && DeleteOpened(root, path, 0);
        }

        private static bool UnderTemp(string path)
        {
            try
            {
                string temp = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()).TrimEnd('\\') + "\\";
                return System.IO.Path.GetFullPath(path).StartsWith(temp, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Opens <paramref name="path"/> as itself, a link as the
        /// link. Null when it cannot be opened.</summary>
        internal static SafeFileHandle OpenByPath(string path, uint access = DeleteAccess | ListAccess)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var handle = CreateFileW(path, access, ShareAll, IntPtr.Zero, OpenExisting, FlagBackupSemantics | FlagOpenReparsePoint, IntPtr.Zero);
            if (!handle.IsInvalid) return handle;
            handle.Dispose();
            return null;
        }

        /// <summary>Opens the entry <paramref name="name"/> of the folder
        /// <paramref name="folder"/> holds, relative to that handle and as
        /// itself, whatever its path leads to now. Null when it cannot be
        /// opened.</summary>
        internal static unsafe SafeFileHandle OpenRelative(SafeFileHandle folder, string name, uint access)
        {
            if (folder == null || folder.IsInvalid || string.IsNullOrEmpty(name) || name.IndexOf('\\') >= 0 || name.Length > 32767)
                return null;
            bool added = false;
            try
            {
                folder.DangerousAddRef(ref added);
                fixed (char* chars = name)
                {
                    var text = new UnicodeString
                    {
                        Length = (ushort)(name.Length * 2),
                        MaximumLength = (ushort)(name.Length * 2),
                        Buffer = (IntPtr)chars,
                    };
                    var attributes = new ObjectAttributes
                    {
                        Length = sizeof(ObjectAttributes),
                        RootDirectory = folder.DangerousGetHandle(),
                        ObjectName = (IntPtr)(&text),
                        Attributes = ObjCaseInsensitive,
                    };
                    int status = NtCreateFile(out SafeFileHandle handle, access | Synchronize, ref attributes, out _, IntPtr.Zero, 0,
                        ShareAll, FileOpen, OpenReparsePoint | SynchronousIoNonAlert | OpenForBackupIntent, IntPtr.Zero, 0);
                    if (status >= 0 && handle != null && !handle.IsInvalid) return handle;
                    handle?.Dispose();
                    return null;
                }
            }
            finally
            {
                if (added) folder.DangerousRelease();
            }
        }

        /// <summary>What an open handle is: its attributes and the file's
        /// ID on its volume. False when it cannot be read.</summary>
        internal static bool Identify(SafeFileHandle handle, out uint attributes, out ulong volume, out ulong fileId, out uint links)
        {
            attributes = 0;
            volume = fileId = 0;
            links = 0;
            if (handle == null || handle.IsInvalid || !GetFileInformationByHandle(handle, out var info)) return false;
            attributes = info.FileAttributes;
            volume = info.VolumeSerialNumber;
            fileId = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
            links = info.NumberOfLinks;
            return true;
        }

        /// <summary>The names a folder holds, read through its own handle.
        /// Null when they cannot be read.</summary>
        internal static unsafe List<string> List(SafeFileHandle folder)
        {
            var names = new List<string>();
            var buffer = new byte[64 * 1024];
            bool first = true;
            fixed (byte* start = buffer)
            {
                while (true)
                {
                    if (!GetFileInformationByHandleEx(folder, first ? FileFullDirectoryRestartInfo : FileFullDirectoryInfo, (IntPtr)start, (uint)buffer.Length))
                    {
                        int error = Marshal.GetLastWin32Error();
                        return error == ErrorNoMoreFiles || (first && error == ErrorFileNotFound) ? names : null;
                    }
                    first = false;
                    int at = 0;
                    while (true)
                    {
                        byte* entry = start + at;
                        int next = *(int*)entry;
                        int nameBytes = *(int*)(entry + 60);
                        if (at + 68 + nameBytes > buffer.Length) return null;
                        string name = new string((char*)(entry + 68), 0, nameBytes / 2);
                        if (name != "." && name != "..") names.Add(name);
                        if (next == 0) break;
                        at += next;
                    }
                }
            }
        }

        private static bool DeleteOpened(SafeFileHandle handle, string path, int depth)
        {
            if (!Identify(handle, out uint attributes, out _, out _, out _)) return false;
            bool folder = (attributes & AttributeDirectory) != 0 && (attributes & AttributeReparsePoint) == 0;
            if (!folder) return MarkDeleted(handle);
            if (depth >= MostDepth) return false;
            bool all = true;
            // Another account may add entries while this one deletes, so the
            // folder is listed again until it is empty, a few times at most.
            for (int pass = 0; pass < MostPasses; pass++)
            {
                var names = List(handle);
                if (names == null) return false;
                if (names.Count == 0) break;
                ListedSeam?.Invoke(path);
                foreach (string name in names)
                {
                    using var child = OpenRelative(handle, name, DeleteAccess | ListAccess) ?? OpenRelative(handle, name, DeleteAccess);
                    if (child == null)
                    {
                        all = false;
                        continue;
                    }
                    if (!DeleteOpened(child, path + "\\" + name, depth + 1)) all = false;
                }
                if (!all) break;
            }
            return MarkDeleted(handle) && all;
        }

        /// <summary>How many times a folder is listed while its entries are
        /// deleted.</summary>
        private const int MostPasses = 4;

        /// <summary>Deletes what the handle names: at once where the volume
        /// takes the POSIX form, read-only or not, and when the last handle
        /// closes otherwise.</summary>
        internal static unsafe bool MarkDeleted(SafeFileHandle handle)
        {
            uint flags = DispositionDelete | DispositionPosixSemantics | DispositionIgnoreReadOnly;
            if (SetFileInformationByHandle(handle, FileDispositionInfoEx, (IntPtr)(&flags), sizeof(uint))) return true;
            byte delete = 1;
            return SetFileInformationByHandle(handle, FileDispositionInfo, (IntPtr)(&delete), 1);
        }

        internal const uint DeleteAccess = 0x00010000 | 0x00000080; // DELETE | FILE_READ_ATTRIBUTES
        internal const uint ListAccess = 0x00000001; // FILE_LIST_DIRECTORY
        private const uint Synchronize = 0x00100000;
        private const uint ShareAll = 0x7;
        private const uint OpenExisting = 3;
        private const uint FlagBackupSemantics = 0x02000000;
        private const uint FlagOpenReparsePoint = 0x00200000;
        internal const uint AttributeDirectory = 0x10;
        internal const uint AttributeReparsePoint = 0x400;
        private const uint ObjCaseInsensitive = 0x40;
        private const uint FileOpen = 1;
        private const uint OpenReparsePoint = 0x00200000;
        private const uint SynchronousIoNonAlert = 0x00000020;
        private const uint OpenForBackupIntent = 0x00004000;
        private const int FileFullDirectoryInfo = 14;
        private const int FileFullDirectoryRestartInfo = 15;
        private const int FileDispositionInfo = 4;
        private const int FileDispositionInfoEx = 21;
        private const uint DispositionDelete = 0x1;
        private const uint DispositionPosixSemantics = 0x2;
        private const uint DispositionIgnoreReadOnly = 0x10;
        private const int ErrorFileNotFound = 2;
        private const int ErrorNoMoreFiles = 18;

        [StructLayout(LayoutKind.Sequential)]
        private struct UnicodeString
        {
            public ushort Length;
            public ushort MaximumLength;
            public IntPtr Buffer;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ObjectAttributes
        {
            public int Length;
            public IntPtr RootDirectory;
            public IntPtr ObjectName;
            public uint Attributes;
            public IntPtr SecurityDescriptor;
            public IntPtr SecurityQualityOfService;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoStatusBlock
        {
            public IntPtr Status;
            public IntPtr Information;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public uint CreationTimeLow, CreationTimeHigh, LastAccessTimeLow, LastAccessTimeHigh, LastWriteTimeLow, LastWriteTimeHigh;
            public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("ntdll.dll")]
        private static extern int NtCreateFile(out SafeFileHandle handle, uint access, ref ObjectAttributes attributes, out IoStatusBlock status,
            IntPtr allocationSize, uint fileAttributes, uint share, uint disposition, uint options, IntPtr ea, uint eaLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation info);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, IntPtr buffer, uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, IntPtr buffer, uint size);
    }
}
