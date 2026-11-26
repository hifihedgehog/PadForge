using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using PadForge.Common;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A folder another account may change, deleted by PadForge with rights
    /// (#597): the protected folder's set-aside folders in %ProgramData%
    /// (UnpackFolder.CleanUp) and the old staging folder in the user's temp
    /// folder (UpdateService.CleanLegacyStaging). A program of the user that
    /// swaps a subfolder for a junction while the delete runs must not lead
    /// it into the junction's target. Each test makes its own folders under
    /// a temp folder of its own, and its junctions in-process.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class HandleTreeTests
    {
        /// <summary>A junction at <paramref name="link"/>, an empty folder
        /// made here, to <paramref name="target"/>: the mount point a
        /// program without rights may make (FSCTL_SET_REPARSE_POINT with
        /// IO_REPARSE_TAG_MOUNT_POINT, as mklink /J makes it).</summary>
        internal static void MakeJunction(string link, string target)
        {
            Directory.CreateDirectory(link);
            string substitute = @"\??\" + Path.GetFullPath(target);
            string print = Path.GetFullPath(target);
            byte[] sub = Encoding.Unicode.GetBytes(substitute);
            byte[] pr = Encoding.Unicode.GetBytes(print);
            int pathBytes = sub.Length + 2 + pr.Length + 2;
            var data = new byte[8 + 8 + pathBytes];
            BitConverter.GetBytes(0xA0000003u).CopyTo(data, 0);
            BitConverter.GetBytes((ushort)(8 + pathBytes)).CopyTo(data, 4);
            BitConverter.GetBytes((ushort)0).CopyTo(data, 8);
            BitConverter.GetBytes((ushort)sub.Length).CopyTo(data, 10);
            BitConverter.GetBytes((ushort)(sub.Length + 2)).CopyTo(data, 12);
            BitConverter.GetBytes((ushort)pr.Length).CopyTo(data, 14);
            sub.CopyTo(data, 16);
            pr.CopyTo(data, 16 + sub.Length + 2);
            using var handle = CreateFileW(link, 0x40000000 /* GENERIC_WRITE */, 0x7, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
            Assert.False(handle.IsInvalid, "the junction's folder did not open: " + Marshal.GetLastWin32Error());
            Assert.True(DeviceIoControl(handle, 0x000900A4, data, data.Length, IntPtr.Zero, 0, out _, IntPtr.Zero),
                "the junction was not made: " + Marshal.GetLastWin32Error());
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, int inputSize, IntPtr output, int outputSize,
            out int returned, IntPtr overlapped);

        internal static string OwnFolder(string tag)
        {
            string folder = Path.Combine(Path.GetTempPath(), "PadForge.Tests.HandleTree." + tag + "." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            return folder;
        }

        /// <summary>A tree to delete, with a subfolder, and a canary folder
        /// beside it holding a file of the same name as the subfolder's.</summary>
        private static (string Tree, string Sub, string Canary) Plant(string root, string treeName)
        {
            string tree = Path.Combine(root, treeName);
            string sub = Path.Combine(tree, "sub");
            Directory.CreateDirectory(sub);
            File.WriteAllText(Path.Combine(sub, "keep.txt"), "the tree's");
            File.WriteAllText(Path.Combine(tree, "top.txt"), "the tree's");
            string canary = Path.Combine(root, "canary");
            Directory.CreateDirectory(canary);
            File.WriteAllText(Path.Combine(canary, "keep.txt"), "not the tree's");
            return (tree, sub, canary);
        }

        /// <summary>Once the tree is listed, the program swaps the subfolder
        /// for a junction to the canary.</summary>
        private static Action<string> SwapOnce(string tree, string sub, string canary)
        {
            bool swapped = false;
            return listed =>
            {
                if (swapped || !string.Equals(Path.GetFullPath(listed).TrimEnd('\\'), Path.GetFullPath(tree).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;
                swapped = true;
                Directory.Move(sub, sub + ".moved");
                MakeJunction(sub, canary);
            };
        }

        private static void Clean(string root)
        {
            try
            {
                foreach (string dir in Directory.GetDirectories(root, "*", SearchOption.AllDirectories))
                    if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) Directory.Delete(dir);
                Directory.Delete(root, recursive: true);
            }
            catch { }
        }

        /// <summary>The set-aside folders in %ProgramData% are made by any
        /// account, and PadForge with rights deletes them at every start. A
        /// subfolder swapped for a junction once the folder was listed is
        /// deleted as the link, and the canary's file stays. The control: the
        /// rest of the folder is gone.</summary>
        [Fact]
        public void TheSetAsideFolders_AreDeletedByHandle_AndAJunctionSwappedInIsDeletedAsTheLink()
        {
            string parent = OwnFolder("Unpack");
            string root = Path.Combine(parent, "PadForge");
            Directory.CreateDirectory(Path.Combine(root, "Natives"));
            var planted = Plant(parent, "PadForge.untrusted-1f-2e-0");
            ProtectedFolder.RootSeam = root;
            HandleTree.ListedSeam = null;
            try
            {
                string aside = planted.Tree + ".removing-" + Environment.ProcessId;
                HandleTree.ListedSeam = SwapOnce(aside, Path.Combine(aside, "sub"), planted.Canary);
                UnpackFolder.CleanUp(Path.Combine(root, "Natives", "PadForge", "current"), Path.Combine(root, "Natives"));
                Assert.True(File.Exists(Path.Combine(planted.Canary, "keep.txt")), "the delete followed the junction into the canary");
                Assert.False(Directory.Exists(planted.Tree));
                Assert.False(Directory.Exists(aside));

                // A name MakeSound never gives is another account's folder,
                // and is left as it is.
                string other = Path.Combine(parent, "PadForge.untrusted-x");
                Directory.CreateDirectory(other);
                UnpackFolder.CleanUp(Path.Combine(root, "Natives", "PadForge", "current"), Path.Combine(root, "Natives"));
                Assert.True(Directory.Exists(other));
                Assert.True(UnpackFolder.IsSetAside(Path.GetFileName(ProtectedFolder.AsideName(root, "1f-2e", 3)), "PadForge"));
                Assert.True(UnpackFolder.IsSetAside("Natives.untrusted-1f-2e-0.removing-4242", null));
                Assert.False(UnpackFolder.IsSetAside("PadForge.untrusted-x", "PadForge"));
                Assert.False(UnpackFolder.IsSetAside("Other.untrusted-1f-2e-0", "PadForge"));
            }
            finally
            {
                HandleTree.ListedSeam = null;
                ProtectedFolder.RootSeam = null;
                Clean(parent);
            }
        }

        /// <summary>The old staging folder sits in the user's temp folder,
        /// where any program of the user can make it, and the copy with
        /// rights deletes it. The same swap leaves the canary's file.</summary>
        [Fact]
        public void TheOldStagingFolder_IsDeletedByHandle_AndAJunctionSwappedInIsDeletedAsTheLink()
        {
            string own = Path.Combine(UpdateService.TestScratchRoot, "HandleTree", "Legacy_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(own);
            var planted = Plant(own, "PadForge_Update");
            string staging = Path.Combine(own, "Protected", "Update");
            HandleTree.ListedSeam = SwapOnce(planted.Tree, planted.Sub, planted.Canary);
            try
            {
                Assert.True(UpdateService.CleanLegacyStaging(@"Q:\NoSuchPadForge\PadForge.exe", staging, planted.Tree));
                Assert.True(File.Exists(Path.Combine(planted.Canary, "keep.txt")), "the delete followed the junction into the canary");
                Assert.False(Directory.Exists(planted.Tree));
            }
            finally
            {
                HandleTree.ListedSeam = null;
                Clean(own);
            }
        }

        /// <summary>A link anywhere in the tree is deleted as the link, a
        /// read-only file goes, and a tree deeper than the walk goes is left
        /// below its depth.</summary>
        [Fact]
        public void TheWalk_DeletesLinksAsLinks_ReadOnlyFiles_AndStopsAtItsDepth()
        {
            string root = OwnFolder("Walk");
            try
            {
                string tree = Path.Combine(root, "tree");
                Directory.CreateDirectory(tree);
                string canary = Path.Combine(root, "canary");
                Directory.CreateDirectory(canary);
                File.WriteAllText(Path.Combine(canary, "keep.txt"), "x");
                MakeJunction(Path.Combine(tree, "link"), canary);
                string ro = Path.Combine(tree, "ro.txt");
                File.WriteAllText(ro, "x");
                File.SetAttributes(ro, FileAttributes.ReadOnly);
                Assert.True(HandleTree.Delete(tree));
                Assert.False(Directory.Exists(tree));
                Assert.True(File.Exists(Path.Combine(canary, "keep.txt")));

                string deep = Path.Combine(root, "deep");
                string at = deep;
                for (int i = 0; i <= HandleTree.MostDepth; i++) at = Path.Combine(at, "d");
                Directory.CreateDirectory(@"\\?\" + at);
                Assert.False(HandleTree.Delete(deep));
                Assert.True(Directory.Exists(deep));
            }
            finally
            {
                Clean(root);
            }
        }
    }
}
