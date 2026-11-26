using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using PadForge.Common.Recording;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Where a recording waits (#600): the signed-in user's temp folder,
    /// read the same way by PadForge with rights, which writes the recording,
    /// and by the save copy it starts as the user, which checks that a file
    /// lies in that folder before it copies it. PadForge writes there with its
    /// rights, so a temp folder outside the user's profile, or one reached
    /// through a link, is refused. Every folder here is the test's own.
    /// </summary>
    [Collection("RecordingFolderStatics")]
    public class RecordingFolderUserTests
    {
        private sealed class Root : IDisposable
        {
            internal readonly string Dir = Path.Combine(Path.GetTempPath(), "PadForge.Tests.RecFolder." + Guid.NewGuid().ToString("N"));
            internal string Profile => Path.Combine(Dir, "profile");
            internal string LocalTemp => Path.Combine(Profile, "AppData", "Local", "Temp");
            internal string Elsewhere => Path.Combine(Dir, "elsewhere");
            internal readonly Func<RecordingFolder.UserView> Before = RecordingFolder.UserSeam;
            internal readonly Func<Action, bool> BeforeAsUser = RecordingFolder.AsUserSeam;

            internal Root()
            {
                Directory.CreateDirectory(LocalTemp);
                Directory.CreateDirectory(Elsewhere);
            }

            internal void Read(bool shell, params string[] variables)
                => RecordingFolder.UserSeam = () => new RecordingFolder.UserView(shell, variables, Profile);

            public void Dispose()
            {
                RecordingFolder.UserSeam = Before;
                RecordingFolder.AsUserSeam = BeforeAsUser;
                string link = Path.Combine(Profile, "link");
                if (Directory.Exists(link)) Directory.Delete(link);
                Directory.Delete(Dir, true);
            }
        }

        /// <summary>The branch with rights reads the shell's variables, the
        /// one without reads its own, and both take TEMP before TMP, so the
        /// save copy looks in the folder the recording was written to.</summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void BothBranches_TakeTempBeforeTmp(bool shell)
        {
            using var root = new Root();
            string other = Path.Combine(root.Profile, "Other");
            root.Read(shell, "TMP=" + other, "TEMP=" + root.LocalTemp);
            Assert.Equal(Path.Combine(root.LocalTemp, RecordingFolder.Name), RecordingFolder.Get());
            Assert.False(Directory.Exists(Path.Combine(other, RecordingFolder.Name)));
        }

        /// <summary>A TEMP a program of the user pointed outside the user's
        /// profile is refused, and nothing is made there.</summary>
        [Fact]
        public void ATempOutsideTheProfile_IsRefused_AndNoFolderIsMadeThere()
        {
            using var root = new Root();
            root.Read(true, "TEMP=" + root.Elsewhere);
            Assert.Null(RecordingFolder.Get());
            Assert.False(Directory.Exists(Path.Combine(root.Elsewhere, RecordingFolder.Name)));
        }

        /// <summary>With TEMP and TMP outside the profile, the Temp folder
        /// of the user's local app data is the one.</summary>
        [Fact]
        public void ATempOutsideTheProfile_GivesWayToTheLocalTemp()
        {
            using var root = new Root();
            root.Read(true, "TEMP=" + root.Elsewhere, "TMP=" + root.Elsewhere, "LOCALAPPDATA=" + Path.Combine(root.Profile, "AppData", "Local"));
            Assert.Equal(Path.Combine(root.LocalTemp, RecordingFolder.Name), RecordingFolder.Get());
            Assert.False(Directory.Exists(Path.Combine(root.Elsewhere, RecordingFolder.Name)));
        }

        /// <summary>A TEMP inside the profile that is reached through a
        /// junction is refused, wherever the junction leads, and nothing is
        /// made at its target.</summary>
        [Fact]
        public void ATempReachedThroughAJunction_IsRefused()
        {
            using var root = new Root();
            string link = Path.Combine(root.Profile, "link");
            Junction(link, root.Elsewhere);
            Assert.True(RecordingFolder.IsLink(link));
            root.Read(true, "TEMP=" + Path.Combine(link, "Temp"));
            Assert.Null(RecordingFolder.Get());
            Assert.False(Directory.Exists(Path.Combine(root.Elsewhere, "Temp")));
        }

        /// <summary>The folder is made as the user, so a folder swapped for a
        /// junction after the check leads the making nowhere the user could
        /// not write: PadForge with rights makes no folder there.</summary>
        [Fact]
        public void TheFolder_IsMadeAsTheUser()
        {
            using var root = new Root();
            string folder = Path.Combine(root.LocalTemp, RecordingFolder.Name);
            bool madeAsUser = false;
            RecordingFolder.AsUserSeam = work =>
            {
                Assert.False(Directory.Exists(folder), "the folder was made before the user's turn");
                work();
                madeAsUser = Directory.Exists(folder);
                return true;
            };
            root.Read(true, "TEMP=" + root.LocalTemp);
            Assert.Equal(folder, RecordingFolder.Get());
            Assert.True(madeAsUser, "the folder was not made as the user");
        }

        /// <summary>When the folder cannot be made as the user, there is no
        /// folder, and PadForge makes none with its rights.</summary>
        [Fact]
        public void NoUserToMakeItAs_NoFolder()
        {
            using var root = new Root();
            RecordingFolder.AsUserSeam = _ => false;
            root.Read(true, "TEMP=" + root.LocalTemp);
            Assert.Null(RecordingFolder.Get());
            Assert.False(Directory.Exists(Path.Combine(root.LocalTemp, RecordingFolder.Name)));
        }

        /// <summary>No profile read, no folder.</summary>
        [Fact]
        public void NoProfile_NoFolder()
        {
            using var root = new Root();
            RecordingFolder.UserSeam = () => new RecordingFolder.UserView(true, new[] { "TEMP=" + root.LocalTemp }, null);
            Assert.Null(RecordingFolder.Get());
        }

        /// <summary>The profile folder this process's user has, as .NET
        /// reads it, which pins the known folder's id.</summary>
        [Fact]
        public void TheProfileRead_IsTheUsersProfile()
            => Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), RecordingFolder.ProfileOf(IntPtr.Zero));

        /// <summary>With rights, as PadForge runs, the branch reads the
        /// desktop shell's variables and profile through its token when the
        /// shell has no rights, and this process's own otherwise, and finds
        /// the user's temp folder in them.</summary>
        [ElevatedFact]
        public void WithRights_TheShellsVariablesAndProfile_GiveTheUsersTemp()
        {
            bool shellWithoutRights;
            using (var shell = PadForge.Services.Plugins.PluginLauncher.OpenShell())
                shellWithoutRights = shell != null && PadForge.Services.Plugins.PluginLauncher.HasAdministratorRights(shell) == false;
            var view = RecordingFolder.ReadUser();
            Assert.Equal(shellWithoutRights, view.Shell);
            Assert.False(string.IsNullOrEmpty(view.Profile));
            Assert.NotNull(RecordingFolder.TempOf(view.Variables, view.Profile));
        }

        // ── A junction of the test's own ──

        private static void Junction(string link, string target)
        {
            Directory.CreateDirectory(link);
            using var handle = CreateFileW(link, GenericWrite, 0, IntPtr.Zero, OpenExisting, BackupSemantics | OpenReparsePoint, IntPtr.Zero);
            Assert.False(handle.IsInvalid, "the junction's folder could not be opened");
            byte[] substitute = Encoding.Unicode.GetBytes(@"\??\" + Path.GetFullPath(target));
            byte[] print = Encoding.Unicode.GetBytes(Path.GetFullPath(target));
            int pathBytes = substitute.Length + 2 + print.Length + 2;
            var buffer = new byte[8 + 8 + pathBytes];
            BitConverter.GetBytes(MountPointTag).CopyTo(buffer, 0);
            BitConverter.GetBytes((ushort)(8 + pathBytes)).CopyTo(buffer, 4);
            BitConverter.GetBytes((ushort)0).CopyTo(buffer, 8);
            BitConverter.GetBytes((ushort)substitute.Length).CopyTo(buffer, 10);
            BitConverter.GetBytes((ushort)(substitute.Length + 2)).CopyTo(buffer, 12);
            BitConverter.GetBytes((ushort)print.Length).CopyTo(buffer, 14);
            substitute.CopyTo(buffer, 16);
            print.CopyTo(buffer, 16 + substitute.Length + 2);
            Assert.True(DeviceIoControl(handle, SetReparsePoint, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero),
                "the junction was not made: " + Marshal.GetLastWin32Error());
        }

        private const uint GenericWrite = 0x40000000, OpenExisting = 3, BackupSemantics = 0x02000000, OpenReparsePoint = 0x00200000;
        private const uint MountPointTag = 0xA0000003, SetReparsePoint = 0x000900A4;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, int inputSize, IntPtr output, int outputSize, out int returned, IntPtr overlapped);
    }
}
