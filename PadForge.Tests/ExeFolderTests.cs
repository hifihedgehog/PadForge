using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using PadForge.Common;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The folder PadForge.exe runs from (#597): which folder is PadForge's
    /// own, who may change it, the protection a start with rights gives it,
    /// and the refusals for a folder that holds other files.
    /// </summary>
    public class ExeFolderTests
    {
        private static ExeFolder.Item File(string name, int links = 1) => new(name, false, false, links, null);
        private static ExeFolder.Item Folder(string name, params ExeFolder.Item[] children) => new(name, true, false, 1, children);
        private static ExeFolder.Item Link(string name) => new(name, true, true, 1, null);

        [Theory]
        [InlineData("PadForge.exe")]
        [InlineData("padforge.exe")]
        [InlineData("PadForge.xml")]
        [InlineData("PadForge.xml.tmp")]
        [InlineData("PadForge.xml.unreadable-20261123-101010")]
        [InlineData("Settings.xml")]
        [InlineData("crash.log")]
        [InlineData("diagnostics.log")]
        [InlineData("diagnostics.log.old")]
        [InlineData("diag.log")]
        [InlineData("diag.log.old")]
        [InlineData("diag-snapshot-20261123-101010.log")]
        [InlineData("PadForge-recording-20261123-101010.zip")]
        [InlineData("PadForge-recording-20261123-101010.zip.tmp")]
        [InlineData("PadForge-recording-20261123-101010.log.partial")]
        [InlineData("PadForge.firstrun")]
        [InlineData("Arcade.pfsounds")]
        [InlineData("Arcade.pficons")]
        [InlineData("Night.pftheme")]
        [InlineData("Racing.pfprofile")]
        [InlineData("Racing.pfprofile.tmp")]
        public void PadForgesOwnFiles_AreKnownByName(string name)
            => Assert.True(ExeFolder.IsPadForgeFile(name, "PadForge.exe"));

        [Theory]
        [InlineData("version.dll")]
        [InlineData("PadForge.exe.config")]
        [InlineData("DSX.exe")]
        [InlineData("setup.exe")]
        [InlineData("notes.txt")]
        [InlineData("PadForge.dll")]
        [InlineData("diag-snapshot-x.txt")]
        [InlineData("persona-diag.log")]
        [InlineData("")]
        public void AnythingElse_IsNotPadForges(string name)
            => Assert.False(ExeFolder.IsPadForgeFile(name, "PadForge.exe"));

        [Fact]
        public void ARenamedExe_IsPadForgesUnderItsOwnName()
        {
            Assert.True(ExeFolder.IsPadForgeFile("PadForge (2).exe", "PadForge (2).exe"));
            Assert.True(ExeFolder.IsOwn(new[] { File("PadForge (2).exe"), File("PadForge.xml") }, "PadForge (2).exe"));
        }

        [Fact]
        public void AFolderOfPadForgesFilesAlone_IsItsOwn()
        {
            Assert.True(ExeFolder.IsOwn(new[] { File("PadForge.exe") }, "PadForge.exe"));
            Assert.True(ExeFolder.IsOwn(new[]
            {
                File("PadForge.exe"), File("PadForge.xml"), File("crash.log"),
                Folder("GameLooks", File("570.png"), File("570.png.tmp-1a2b3c4d")),
            }, "PadForge.exe"));
        }

        [Fact]
        public void AFolderThatHoldsAnythingElse_IsShared()
        {
            // Downloads.
            Assert.False(ExeFolder.IsOwn(new[] { File("PadForge.exe"), File("setup.exe") }, "PadForge.exe"));
            // A library beside the exe, the very thing the rule keeps out.
            Assert.False(ExeFolder.IsOwn(new[] { File("PadForge.exe"), File("version.dll") }, "PadForge.exe"));
            // Any folder but GameLooks, and GameLooks holding a folder.
            Assert.False(ExeFolder.IsOwn(new[] { File("PadForge.exe"), Folder("Profiles") }, "PadForge.exe"));
            Assert.False(ExeFolder.IsOwn(new[] { File("PadForge.exe"), Folder("GameLooks", Folder("x")) }, "PadForge.exe"));
            // A link, whose list is another folder's, and a hard link, whose
            // list is another file's.
            Assert.False(ExeFolder.IsOwn(new[] { File("PadForge.exe"), Link("GameLooks") }, "PadForge.exe"));
            Assert.False(ExeFolder.IsOwn(new[] { File("PadForge.exe"), File("PadForge.xml", links: 2) }, "PadForge.exe"));
            // A folder without the exe is not where PadForge runs from.
            Assert.False(ExeFolder.IsOwn(new[] { File("PadForge.xml") }, "PadForge.exe"));
            Assert.False(ExeFolder.IsOwn(null, "PadForge.exe"));
        }

        [Fact]
        public void ReadingAFolder_ReportsWhatTheRuleNeeds()
        {
            string dir = Path.Combine(UpdateService.TestScratchRoot, "exefolder-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(dir, "GameLooks"));
            try
            {
                System.IO.File.WriteAllText(Path.Combine(dir, "PadForge.exe"), "x");
                System.IO.File.WriteAllText(Path.Combine(dir, "PadForge.xml"), "x");
                System.IO.File.WriteAllText(Path.Combine(dir, "GameLooks", "570.png"), "x");
                var items = ExeFolder.Read(dir);
                Assert.True(ExeFolder.IsOwn(items, "PadForge.exe"));
                Assert.All(items.Where(i => !i.IsFolder), i => Assert.Equal(1, i.Links));
                Assert.Equal(4, ExeFolder.FilesOf(dir, items).Count);

                System.IO.File.WriteAllText(Path.Combine(dir, "version.dll"), "x");
                Assert.False(ExeFolder.IsOwn(ExeFolder.Read(dir), "PadForge.exe"));
                Assert.Equal(ExeFolder.Standing.Shared, ExeFolder.Check(Path.Combine(dir, "PadForge.exe")));
                Assert.False(ExeFolder.MayHoldTasks(Path.Combine(dir, "PadForge.exe")));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        /// <summary>A folder this test made in its user's temp folder is
        /// PadForge's own by its names and changeable by its user, so a task
        /// may not run from it, and the test host protects nothing.</summary>
        [Fact]
        public void AnOwnFolderOthersMayChange_HoldsNoTask_AndTheTestHostProtectsNothing()
        {
            string dir = Path.Combine(UpdateService.TestScratchRoot, "exefolder-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string exe = Path.Combine(dir, "PadForge.exe");
                System.IO.File.WriteAllText(exe, "x");
                var items = ExeFolder.Read(dir);
                Assert.True(ExeFolder.IsOwn(items, "PadForge.exe"));
                Assert.True(ExeFolder.OthersMayChange(dir, ExeFolder.FilesOf(dir, items)));
                Assert.False(ExeFolder.MayHoldTasks(exe));
                Assert.True(ExeFolder.OffLimitsForTest);
                Assert.False(ExeFolder.Protect(dir, items));
                Assert.Equal(ExeFolder.Standing.Unprotected, ExeFolder.Check(exe));
                Assert.Equal("test host", ExeFolder.CopyTo(Path.Combine(dir, "target"), exe, null).Error);
                Assert.False(Directory.Exists(Path.Combine(dir, "target")));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        /// <summary>A move that could not make its folder says so, and only a
        /// folder that holds files that are not PadForge's is called that.</summary>
        [Fact]
        public void AMoveThatCouldNotMakeItsFolder_IsNotTakenForAFolderOfOtherFiles()
        {
            string dir = Path.Combine(UpdateService.TestScratchRoot, "exefolder-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string exe = Path.Combine(dir, "PadForge.exe");
                System.IO.File.WriteAllText(exe, "x");
                string aFile = Path.Combine(dir, "a-file");
                System.IO.File.WriteAllText(aFile, "x");
                var notMade = ExeFolder.CopyInto(Path.Combine(aFile, "PadForge"), exe, null, null);
                Assert.Null(notMade.NewExe);
                Assert.False(notMade.TargetTaken);

                Assert.Equal(ExeFolder.MoveRefusal.NotMade, notMade.Why);

                // The control: a folder of other files is what the refusal names.
                string taken = Path.Combine(dir, "taken");
                Directory.CreateDirectory(taken);
                System.IO.File.WriteAllText(Path.Combine(taken, "notes.txt"), "x");
                Assert.True(ExeFolder.CopyInto(taken, exe, null, null).TargetTaken);

                // No Program Files folder, and a folder others may change that
                // the test host does not protect, each say their own.
                Assert.Equal(ExeFolder.MoveRefusal.NoProgramFiles, ExeFolder.CopyInto(null, exe, null, null).Why);
                string open = Path.Combine(dir, "open");
                Directory.CreateDirectory(open);
                Assert.Equal(ExeFolder.MoveRefusal.NotProtected, ExeFolder.CopyInto(open, exe, null, null).Why);

                // One whole sentence per reason, each its own.
                var lines = Enum.GetValues<ExeFolder.MoveRefusal>().Where(why => why != ExeFolder.MoveRefusal.None)
                    .Select(why => ExeFolderState.ErrorFor(new ExeFolder.MoveResult(null, @"C:\Program Files\PadForge", why))).ToArray();
                Assert.Equal(lines.Length, lines.Distinct().Count());
                Assert.All(lines, line => Assert.StartsWith("PadForge did not move: ", line, StringComparison.Ordinal));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        /// <summary>The move copies the bytes that run. Any program of the
        /// user can rename the running exe in a shared folder and put its own
        /// at the name, so the copy is read from the hold on the running exe,
        /// never from its path again, and checked against those bytes.</summary>
        [Fact]
        public void AMove_CopiesTheBytesThatRun_NotWhateverThePathHoldsNow()
        {
            string dir = Path.Combine(UpdateService.TestScratchRoot, "exefolder-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string exe = Path.Combine(dir, "PadForge.exe");
                System.IO.File.WriteAllText(exe, "the PadForge that runs");
                string staged = Path.Combine(dir, "copy.tmp");
                using (var image = System.IO.File.OpenHandle(exe, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                {
                    System.IO.File.Move(exe, exe + ".renamed");
                    System.IO.File.WriteAllText(exe, "planted");
                    Assert.True(ExeFolder.CopyImage(image, exe, staged));
                }
                Assert.Equal("the PadForge that runs", System.IO.File.ReadAllText(staged));
            }
            finally
            {
                Directory.Delete(dir, true);
            }

            // The hold is taken on the image's file by the name Windows maps
            // it under now: here the test host's own, read as the same file
            // as its path names, shared for reading alone.
            using (var held = ExeFolder.OpenImageOf(GetModuleHandleW(null)))
            {
                Assert.NotNull(held);
                using var byPath = System.IO.File.OpenHandle(Environment.ProcessPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                Assert.True(HandleTree.Identify(held, out _, out ulong volume, out ulong id, out _));
                Assert.True(HandleTree.Identify(byPath, out _, out ulong pathVolume, out ulong pathId, out _));
                Assert.Equal((pathVolume, pathId), (volume, id));
            }
            Assert.Null(ExeFolder.OpenImageOf(IntPtr.Zero));

            // The name Windows gives a mapped file follows a rename, so the
            // hold is taken on the file at its name now, and once held it can
            // no longer be renamed.
            string mappedDir = Path.Combine(UpdateService.TestScratchRoot, "mapped-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(mappedDir);
            try
            {
                string first = Path.Combine(mappedDir, "first.bin"), second = Path.Combine(mappedDir, "second.bin");
                System.IO.File.WriteAllBytes(first, new byte[4096]);
                using var stream = new FileStream(first, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                using var map = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateFromFile(stream, null, 0,
                    System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: true);
                using var view = map.CreateViewAccessor(0, 0, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);
                System.IO.File.Move(first, second);
                IntPtr at = view.SafeMemoryMappedViewHandle.DangerousGetHandle();
                using (var held = ExeFolder.OpenImageOf(at))
                {
                    Assert.NotNull(held);
                    using var byPath = System.IO.File.OpenHandle(second, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    Assert.True(HandleTree.Identify(held, out _, out ulong volume, out ulong id, out _));
                    Assert.True(HandleTree.Identify(byPath, out _, out ulong pathVolume, out ulong pathId, out _));
                    Assert.Equal((pathVolume, pathId), (volume, id));
                    Assert.ThrowsAny<IOException>(() => System.IO.File.Move(second, first));
                }
            }
            finally
            {
                Directory.Delete(mappedDir, true);
            }
            // The test host takes no hold of the app's.
            ExeFolder.HoldRunningImage();
            Assert.Null(ExeFolder.RunningImage);
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(string name);

        /// <summary>The protection sets each entry through a handle opened
        /// relative to its folder's own handle, as itself, and only when it is
        /// still the entry the reading saw. A GameLooks folder swapped for a
        /// junction after the reading is refused, and nothing of the
        /// junction's target is handed on to be set. The test host sets no
        /// owner, so a recording setter stands in for the list's
        /// write.</summary>
        [Fact]
        public void TheProtection_SetsEachEntryThroughItsFoldersHandle_AndRefusesOneSwappedForAJunction()
        {
            string dir = Path.Combine(UpdateService.TestScratchRoot, "protect-" + Guid.NewGuid().ToString("N"));
            string own = Path.Combine(dir, "own"), canary = Path.Combine(dir, "canary");
            Directory.CreateDirectory(Path.Combine(own, ExeFolder.GameLooksFolder));
            Directory.CreateDirectory(canary);
            System.IO.File.WriteAllText(Path.Combine(own, "PadForge.exe"), "x");
            System.IO.File.WriteAllText(Path.Combine(own, ExeFolder.GameLooksFolder, "look.png"), "x");
            System.IO.File.WriteAllText(Path.Combine(canary, "look.png"), "not PadForge's");
            try
            {
                var given = new List<ulong>();
                bool Record(Microsoft.Win32.SafeHandles.SafeFileHandle entry, bool isFolder)
                {
                    Assert.True(HandleTree.Identify(entry, out _, out _, out ulong id, out _));
                    given.Add(id);
                    return true;
                }
                ulong IdOf(string path)
                {
                    using var handle = HandleTree.OpenByPath(path, 0x80);
                    Assert.True(HandleTree.Identify(handle, out _, out _, out ulong id, out _));
                    return id;
                }

                // The control: the folder as it was read, every entry set.
                var items = ExeFolder.Read(own);
                Assert.True(ExeFolder.IsOwn(items, "PadForge.exe"));
                using (var root = HandleTree.OpenByPath(own, 0x80 | HandleTree.ListAccess))
                    Assert.True(ExeFolder.ProtectEntries(root, items, Record));
                Assert.Equal(3, given.Count);
                Assert.Contains(IdOf(Path.Combine(own, ExeFolder.GameLooksFolder, "look.png")), given);

                // GameLooks swapped for a junction to the canary after the reading.
                given.Clear();
                Directory.Move(Path.Combine(own, ExeFolder.GameLooksFolder), Path.Combine(dir, "looks.moved"));
                HandleTreeTests.MakeJunction(Path.Combine(own, ExeFolder.GameLooksFolder), canary);
                using (var root = HandleTree.OpenByPath(own, 0x80 | HandleTree.ListAccess))
                    Assert.False(ExeFolder.ProtectEntries(root, items, Record));
                Assert.DoesNotContain(IdOf(canary), given);
                Assert.DoesNotContain(IdOf(Path.Combine(canary, "look.png")), given);

                // GameLooks swapped for another plain folder: not the entry
                // read, by its ID, so it is refused too.
                Directory.Delete(Path.Combine(own, ExeFolder.GameLooksFolder));
                Directory.CreateDirectory(Path.Combine(own, ExeFolder.GameLooksFolder));
                given.Clear();
                using (var root = HandleTree.OpenByPath(own, 0x80 | HandleTree.ListAccess))
                    Assert.False(ExeFolder.ProtectEntries(root, items, Record));
                Assert.DoesNotContain(IdOf(Path.Combine(own, ExeFolder.GameLooksFolder)), given);
            }
            finally
            {
                string junction = Path.Combine(own, ExeFolder.GameLooksFolder);
                if (Directory.Exists(junction) && (System.IO.File.GetAttributes(junction) & FileAttributes.ReparsePoint) != 0) Directory.Delete(junction);
                Directory.Delete(dir, true);
            }
        }

        /// <summary>With rights, as a start protects an own folder: a
        /// GameLooks folder swapped for a junction once the folder's own list
        /// is set is refused, and the list and owner of the junction's
        /// target's file of the same name stay as they were. The control: the
        /// same folder as it was read is protected, and no other account may
        /// change it.</summary>
        [ElevatedFact]
        public void TheProtection_WithRights_LeavesTheListOfAJunctionsTargetAsItWas()
        {
            string dir = Path.Combine(UpdateService.TestScratchRoot, "protect-" + Guid.NewGuid().ToString("N"));
            string own = Path.Combine(dir, "own"), control = Path.Combine(dir, "control"), canary = Path.Combine(dir, "canary");
            foreach (string folder in new[] { own, control })
            {
                Directory.CreateDirectory(Path.Combine(folder, ExeFolder.GameLooksFolder));
                System.IO.File.WriteAllText(Path.Combine(folder, "PadForge.exe"), "x");
                System.IO.File.WriteAllText(Path.Combine(folder, ExeFolder.GameLooksFolder, "look.png"), "x");
            }
            Directory.CreateDirectory(canary);
            string target = Path.Combine(canary, "look.png");
            System.IO.File.WriteAllText(target, "not PadForge's");
            static string ListOf(string path) => new FileInfo(path).GetAccessControl()
                .GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access);
            string before = ListOf(target);
            try
            {
                var items = ExeFolder.Read(own);
                ExeFolder.BeforeEntriesSeam = _ =>
                {
                    Directory.Move(Path.Combine(own, ExeFolder.GameLooksFolder), Path.Combine(dir, "looks.moved"));
                    HandleTreeTests.MakeJunction(Path.Combine(own, ExeFolder.GameLooksFolder), canary);
                };
                try
                {
                    Assert.False(ExeFolder.ProtectAt(own, items));
                }
                finally
                {
                    ExeFolder.BeforeEntriesSeam = null;
                }
                Assert.Equal(before, ListOf(target));

                Assert.True(ExeFolder.ProtectAt(control, ExeFolder.Read(control)));
                Assert.False(ExeFolder.OthersMayChange(control, ExeFolder.FilesOf(control, ExeFolder.Read(control))));
            }
            finally
            {
                string junction = Path.Combine(own, ExeFolder.GameLooksFolder);
                if (Directory.Exists(junction) && (System.IO.File.GetAttributes(junction) & FileAttributes.ReparsePoint) != 0) Directory.Delete(junction);
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void TheOwnFolder_IsPadForgeUnderProgramFiles()
            => Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PadForge"), ExeFolder.OwnFolderPath());

        /// <summary>The protection, read from its source: the folder takes the
        /// protected folder's list, and each file the owner Administrators and
        /// only what it inherits, under the privileges that set an owner.</summary>
        [Fact]
        public void TheProtection_GivesTheFolderTheProtectedList_AndEachFileItsInheritance()
        {
            string source = AuditDelta20261002Tests.RepoText("PadForge.App", "Services", "ExeFolder.cs");
            Assert.StartsWith("if (OffLimitsForTest) return false;", FenceSource.FirstStatement(source, "internal static bool Protect(string folder, IReadOnlyList<Item> items)"), StringComparison.Ordinal);
            string protect = FenceSource.Body(source, "internal static bool ProtectAt(string folder, IReadOnlyList<Item> items)");
            Assert.Contains("using (Privileges.Hold(\"SeTakeOwnershipPrivilege\", \"SeRestorePrivilege\"))", protect, StringComparison.Ordinal);
            Assert.Contains("if (!SetSecurity(root, ProtectedFolder.Sddl, protect: true)) return false;", protect, StringComparison.Ordinal);
            Assert.Contains("if (!ProtectEntries(root, items, SetInherited)) return false;", protect, StringComparison.Ordinal);
            Assert.Contains("return !OthersMayChange(folder, FilesOf(folder, items));", protect, StringComparison.Ordinal);
            Assert.DoesNotContain("SetAccessControl(", source, StringComparison.Ordinal);
            // What a child inherits in a folder of the protected list: each of
            // its entries, inherited, with the folder's own inheritance.
            var folder = new System.Security.AccessControl.RawSecurityDescriptor(ProtectedFolder.Sddl);
            foreach (var (sddl, flags) in new[]
                     {
                         (ExeFolder.InheritedFileSddl, System.Security.AccessControl.AceFlags.Inherited),
                         (ExeFolder.InheritedFolderSddl, System.Security.AccessControl.AceFlags.Inherited | System.Security.AccessControl.AceFlags.ContainerInherit | System.Security.AccessControl.AceFlags.ObjectInherit),
                     })
            {
                var child = new System.Security.AccessControl.RawSecurityDescriptor(sddl);
                Assert.Equal(folder.Owner, child.Owner);
                Assert.Equal(folder.DiscretionaryAcl.Count, child.DiscretionaryAcl.Count);
                for (int k = 0; k < folder.DiscretionaryAcl.Count; k++)
                {
                    var parentAce = (System.Security.AccessControl.CommonAce)folder.DiscretionaryAcl[k];
                    var childAce = (System.Security.AccessControl.CommonAce)child.DiscretionaryAcl[k];
                    Assert.Equal(parentAce.SecurityIdentifier, childAce.SecurityIdentifier);
                    Assert.Equal(parentAce.AccessMask, childAce.AccessMask);
                    Assert.Equal(flags, childAce.AceFlags);
                }
            }
        }

        /// <summary>The list the protection sets, applied by this test to a
        /// folder of its own: owner aside, it leaves the folder sound, and a
        /// file in it takes it by inheritance.</summary>
        [Fact]
        public void TheProtectedList_AppliedToAFolder_LeavesNoOtherAccountAWayIn()
        {
            string dir = Path.Combine(UpdateService.TestScratchRoot, "exefolder-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var info = new DirectoryInfo(dir);
            var before = info.GetAccessControl();
            try
            {
                string file = Path.Combine(dir, "PadForge.xml");
                System.IO.File.WriteAllText(file, "x");
                var sd = new DirectorySecurity();
                // The test host may run without rights, so the owner stays its
                // own. The list is the protected folder's.
                sd.SetSecurityDescriptorSddlForm(ProtectedFolder.Sddl.Replace("O:BA", string.Empty), AccessControlSections.Access);
                info.SetAccessControl(sd);
                var rules = info.GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
                Assert.True(info.GetAccessControl().AreAccessRulesProtected);
                Assert.Equal(3, rules.Length);
                Assert.All(rules, r => Assert.False(r.IsInherited));
                var fileRules = new FileInfo(file).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
                Assert.All(fileRules, r => Assert.True(r.IsInherited));
                Assert.All(fileRules.Where(r => ((SecurityIdentifier)r.IdentityReference).Value == "S-1-5-32-545"),
                    r => Assert.Equal(FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, r.FileSystemRights));
            }
            finally
            {
                // Its owner may always set its list back.
                var open = new DirectorySecurity();
                open.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                info.SetAccessControl(open);
                Directory.Delete(dir, true);
            }
        }
    }

    /// <summary>Where the restarted copy's libraries are, and the folders the
    /// protected folder keeps for what PadForge writes and runs (#597).</summary>
    public class UnpackFolderTests
    {
        [Fact]
        public void TheUnpackFolder_IsTheOneDirectlyBelowTheBaseAndTheExesName()
        {
            string natives = @"C:\ProgramData\PadForge\Natives";
            var modules = new[]
            {
                @"C:\Program Files\PadForge\PadForge.exe",
                @"C:\WINDOWS\SYSTEM32\ntdll.dll",
                @"C:\ProgramData\PadForge\Natives\Other\abc\wpfgfx_cor3.dll",
                @"C:\ProgramData\PadForge\Natives\PadForge\abc\deeper\x.dll",
                @"C:\ProgramData\PadForge\Natives\PadForge\B5tV2QeLmNcX\wpfgfx_cor3.dll",
            };
            Assert.Equal(@"C:\ProgramData\PadForge\Natives\PadForge\B5tV2QeLmNcX", UnpackFolder.Find(modules, natives, "PadForge"));
            Assert.Null(UnpackFolder.Find(modules.Take(4), natives, "PadForge"));
            Assert.Null(UnpackFolder.Find(modules, null, "PadForge"));
        }

        /// <summary>The user's temp folder is never searched for a folder to
        /// trust: before #597 the newest folder there with an xinput1_4.dll
        /// went on the DLL search path.</summary>
        [Fact]
        public void TheDllSearchPath_TakesOnlyThisProcesssOwnFolder()
        {
            string app = AuditDelta20261002Tests.RepoText("PadForge.App", "App.xaml.cs");
            Assert.Contains("string extractionDir = Services.UnpackFolder.Current();", app, StringComparison.Ordinal);
            Assert.DoesNotContain("Path.Combine(Path.GetTempPath(), \".net\", \"PadForge\")", app, StringComparison.Ordinal);
            Assert.DoesNotContain("xinput1_4.dll\")))", app, StringComparison.Ordinal);
        }

        [Fact]
        public void TheTestHost_HasNoWorkFolder()
        {
            Assert.Null(ProtectedWork.Folder("GiveBack"));
            Assert.Null(ProtectedWork.NewFolder("HidHide"));
            Assert.Throws<IOException>(() => ProtectedWork.NewFolderOrThrow("HidHide"));
            // Nothing outside the Work folder is ever deleted by it.
            string dir = Path.Combine(UpdateService.TestScratchRoot, "work-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            ProtectedWork.Remove(dir);
            Assert.True(Directory.Exists(dir));
            Directory.Delete(dir);
        }

        [Fact]
        public void TheDsxStandIn_HasNoFolderInTheTestHost()
        {
            Assert.Null(DsxStandIn.Folder);
            Assert.Null(DsxStandIn.ExePath);
            string source = AuditDelta20261002Tests.RepoText("PadForge.App", "Services", "DsxStandIn.cs");
            Assert.Contains("internal static string Folder => ProtectedWork.Folder(\"DSX\");", source, StringComparison.Ordinal);
            Assert.Contains("using (var pin = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))", source, StringComparison.Ordinal);
            Assert.Contains("process = ShellLaunch.StartOwnHelper(psi);", source, StringComparison.Ordinal);
        }
    }

    /// <summary>Programs PadForge starts for the user start with the user's
    /// rights, through the desktop shell (#597).</summary>
    [Collection("SettingsManagerStatics")]
    public class ShellLaunchUserTests : IDisposable
    {
        private readonly List<ShellLaunch.UserStart> _starts = new();

        public ShellLaunchUserTests() => ShellLaunch.UserShellSeam = s => { _starts.Add(s); return null; };

        public void Dispose() => ShellLaunch.UserShellSeam = null;

        /// <summary>The Falcon card's download button: a start the desktop
        /// shell refused puts its reason on the status line, as a failed start
        /// did before every start went through the shell. The shell's start
        /// throws nothing, so the reason comes back from it.</summary>
        [Fact]
        public void AFalconDownloadTheShellRefused_PutsItsReasonOnTheStatusLine()
        {
            const string refused = "the desktop shell refused the start (0x80004005)";
            ShellLaunch.UserStart asked = null;
            ShellLaunch.UserShellSeam = start => { asked = start; return refused; };
            var status = new List<string>();
            MainWindow.OpenFalconDownload(status.Add);
            Assert.Equal(MainWindow.FalconSdkDownloadUrl, asked?.File);
            var line = Assert.Single(status);
            Assert.Contains(refused, line, StringComparison.Ordinal);

            // The control: a start the shell took says nothing.
            ShellLaunch.UserShellSeam = _ => null;
            status.Clear();
            MainWindow.OpenFalconDownload(status.Add);
            Assert.Empty(status);
        }

        [Fact]
        public void AProgram_GoesToTheDesktopShell_WithItsArgumentsAndFolder()
        {
            Assert.Null(ShellLaunch.Start(new ProcessStartInfo(@"C:\Tools\tool.exe", "--fast \"a b\"")
            {
                UseShellExecute = true,
                WorkingDirectory = @"C:\Tools",
                WindowStyle = ProcessWindowStyle.Maximized,
            }));
            var start = Assert.Single(_starts);
            Assert.Equal(@"C:\Tools\tool.exe", start.File);
            Assert.Equal("--fast \"a b\"", start.Arguments);
            Assert.Equal(@"C:\Tools", start.Folder);
            Assert.Equal(3, start.Show);
        }

        [Fact]
        public void APageAndAFolder_GoToTheDesktopShell()
        {
            ShellLaunch.Start(new ProcessStartInfo("https://padforge.org/docs") { UseShellExecute = true });
            ShellLaunch.Start(ShellLaunch.Explorer, "/select,\"C:\\x\\y.log\"");
            Assert.Equal("https://padforge.org/docs", _starts[0].File);
            Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), _starts[1].File);
            Assert.Equal("/select,\"C:\\x\\y.log\"", _starts[1].Arguments);
        }

        [Fact]
        public void WithNoDesktopShell_NothingStarts_AndTheReasonComesBack()
        {
            ShellLaunch.UserShellSeam = _ => "no desktop shell is running";
            Assert.False(ShellLaunch.AsTheUser(new ProcessStartInfo("notepad.exe"), out string why));
            Assert.Equal("no desktop shell is running", why);
        }

        [Theory]
        [InlineData(new[] { "a" }, "a")]
        [InlineData(new[] { "a b", "c" }, "\"a b\" c")]
        [InlineData(new[] { "say \"hi\"" }, "\"say \\\"hi\\\"\"")]
        [InlineData(new[] { @"C:\dir\" }, @"C:\dir\")]
        [InlineData(new[] { @"C:\my dir\" }, "\"C:\\my dir\\\\\"")]
        [InlineData(new[] { "" }, "\"\"")]
        public void AnArgumentList_IsQuotedAsTheCRuntimeReadsItBack(string[] list, string line)
        {
            var psi = new ProcessStartInfo("x.exe");
            foreach (string a in list) psi.ArgumentList.Add(a);
            Assert.Equal(line, ShellLaunch.ArgumentsOf(psi));
        }

        [Fact]
        public void EveryStartForTheUser_GoesThroughTheShell_AndNoneWithPadForgesRights()
        {
            string shell = AuditDelta20261002Tests.RepoText("PadForge.App", "Common", "ShellLaunch.cs");
            Assert.Empty(System.Text.RegularExpressions.Regex.Matches(shell, @"\bProcess\.Start\("));
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(shell, @"\bRightsVariables\.Start\("));
            Assert.Contains("internal static Process StartOwnHelper(ProcessStartInfo psi)\r\n            => OffLimitsForTest ? null : PadForge.Services.RightsVariables.Start(psi);", shell.Replace("\r\n", "\n").Replace("\n", "\r\n"), StringComparison.Ordinal);
            string macros = AuditDelta20261002Tests.RepoText("PadForge.App", "Common", "Input", "InputManager.Step4b.EvaluateMacros.cs");
            Assert.Contains("if (!PadForge.Common.ShellLaunch.AsTheUser(psi, out string why))", macros, StringComparison.Ordinal);
            // Only PadForge's own helpers start with its rights.
            var helpers = new List<string>();
            foreach (string project in FenceSource.Projects)
                foreach (string path in OffLimitsSource.Sources(project))
                    if (System.IO.File.ReadAllText(path).Contains("ShellLaunch.StartOwnHelper(", StringComparison.Ordinal))
                        helpers.Add(Path.GetFileName(path));
            // The library host is one too, started so only when the shell's
            // user has no lower right (UAC off, or the built-in Administrator),
            // where every program of the user has full rights.
            Assert.Equal(new[] { "App.xaml.cs", "DsxStandIn.cs", "NativeHost.cs" }, helpers.OrderBy(h => h, StringComparer.Ordinal).ToArray());
            string host = OffLimitsSource.Read("PadForge.App/Services/NativeHost/NativeHost.cs");
            Assert.True(host.IndexOf("if (StartsAsOwnHelper(launch.Refusal))", StringComparison.Ordinal)
                        < host.IndexOf("Common.ShellLaunch.StartOwnHelper(psi)", StringComparison.Ordinal));
            Assert.Contains("=> refusal == PluginRefusal.ShellElevated && (NoLowerRightSeam?.Invoke() ?? PluginLauncher.ShellUserHasNoLowerRight());", host, StringComparison.Ordinal);
        }
    }

    /// <summary>The logon task's program, read from its definition (#597).</summary>
    public class StartupTaskTests
    {
        [Fact]
        public void TheTasksProgram_IsItsFirstCommand()
        {
            string xml = "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n<Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">"
                + "<Actions Context=\"Author\"><Exec><Command>\"C:\\PadForge\\PadForge.exe\"</Command></Exec></Actions></Task>";
            Assert.Equal(@"C:\PadForge\PadForge.exe", StartupHelper.TaskExe(xml));
            Assert.Equal(@"C:\PadForge\PadForge.exe", StartupHelper.TaskExe("\r\n" + xml));
            Assert.Null(StartupHelper.TaskExe(null));
            Assert.Null(StartupHelper.TaskExe("not xml"));
            Assert.Null(StartupHelper.TaskExe("<Task><Actions/></Task>"));
        }

        [Fact]
        public void SchtasksIsStarted_ByItsFullPath()
        {
            Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"), StartupHelper.SchtasksPath);
            string source = AuditDelta20261002Tests.RepoText("PadForge.App", "Common", "StartupHelper.cs");
            Assert.DoesNotContain("FileName = \"schtasks.exe\"", source, StringComparison.Ordinal);
            // From a shared folder the switch is refused.
            string set = FenceSource.Body(source, "public static bool SetStartupEnabled(bool enabled)");
            Assert.Contains("if (!ExeFolder.MayHoldTasks(exe))", set, StringComparison.Ordinal);
        }

        [Fact]
        public void TheTestHost_TouchesNoLogonTask()
        {
            Assert.Equal(StartupHelper.TaskRemoval.None, StartupHelper.AuditTask());
            Assert.True(StartupHelper.RepointTask(@"C:\nowhere\PadForge.exe"));
        }
    }
}

namespace PadForge.Tests
{
    /// <summary>Give-back tasks left from before #597 that run PadForge.exe
    /// from a folder other accounts may change are made again without it, and
    /// the move points them at the new exe.</summary>
    [Collection("SettingsManagerStatics")]
    public class GiveBackTaskAuditTests : System.IDisposable
    {
        private const string Touchpad = @"HID\GXTP5100&Col02\4&c834cd3&0&0001";
        private const string Bind = @"USB\VID_046D&PID_C31C";
        private readonly System.Collections.Generic.List<string> _calls = new();
        private readonly string _definitions = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PadForgeTests", "audit-" + System.Guid.NewGuid().ToString("N"));
        private readonly System.Collections.Generic.Dictionary<string, string> _created = new();

        public GiveBackTaskAuditTests()
        {
            GiveBackTask.DefinitionFolderSeam = () => _definitions;
            GiveBackTask.FileExistsSeam = _ => false;
            GiveBackTask.ExeSizeSeam = _ => -1;
            GiveBackTask.ExtractBaseSeam = () => null;
            GiveBackTask.RunSeam = args =>
            {
                _calls.Add(args);
                int at = args.IndexOf("/XML \"", System.StringComparison.Ordinal);
                if (at >= 0)
                {
                    at += 6;
                    string path = args[at..args.IndexOf('"', at)];
                    _created[args] = System.IO.File.ReadAllText(path, System.Text.Encoding.Unicode);
                }
                return 0;
            };
        }

        public void Dispose()
        {
            GiveBackTask.OutputSeam = null;
            GiveBackTask.RunSeam = null;
            GiveBackTask.DefinitionFolderSeam = null;
            GiveBackTask.FileExistsSeam = null;
            GiveBackTask.ExeSizeSeam = null;
            GiveBackTask.ExtractBaseSeam = null;
            GiveBackTask.MayRunExeSeam = null;
            try { System.IO.Directory.Delete(_definitions, true); } catch { }
        }

        private static string Definition(string exe, string runSwitch, string id, string name)
            => GiveBackTask.TaskXml(GiveBackTask.Action(name, exe, runSwitch, id, null, @"C:\Windows\System32"), @"C:\Windows\System32", false);

        [Fact]
        public void TheTaskList_YieldsPadForgesGiveBackTasks()
        {
            string csv = "\"\\PadForge\",\"N/A\",\"Ready\"\r\n\"\\PadForge Give Back 0011223344556677\",\"N/A\",\"Ready\"\r\n"
                + "\"\\Microsoft\\Other\",\"N/A\",\"Ready\"\r\n\"\\PadForge Give Back 0011223344556677\",\"N/A\",\"Ready\"\r\n";
            Assert.Equal(new[] { "PadForge Give Back 0011223344556677" }, GiveBackTask.TaskNamesIn(csv));
            Assert.Empty(GiveBackTask.TaskNamesIn(null));
        }

        [Fact]
        public void ATasksRun_IsReadFromItsDefinition()
        {
            string name = GiveBackTask.NameOf(Touchpad);
            var run = GiveBackTask.RunIn(Definition(@"C:\Users\someone\Downloads\PadForge.exe", GiveBackRun.TouchpadSwitch, Touchpad, name));
            Assert.NotNull(run);
            Assert.Equal(@"C:\Users\someone\Downloads\PadForge.exe", run.Value.Exe);
            Assert.Equal(GiveBackRun.TouchpadSwitch, run.Value.Switch);
            Assert.Equal(Touchpad, run.Value.Id);
            Assert.Null(GiveBackTask.RunIn(Definition(null, GiveBackRun.TouchpadSwitch, Touchpad, name).Replace("--give-back", "--other")));
            Assert.Null(GiveBackTask.RunIn("garbage"));
        }

        [Fact]
        public void ATouchpadsTask_FromASharedFolder_WithNoCli_IsRemoved()
        {
            string name = GiveBackTask.NameOf(Touchpad);
            GiveBackTask.OutputSeam = args => args.Contains("/FO CSV", System.StringComparison.Ordinal)
                ? "\"\\" + name + "\",\"N/A\",\"Ready\"\r\n"
                : Definition(@"C:\Users\someone\Downloads\PadForge.exe", GiveBackRun.TouchpadSwitch, Touchpad, name);
            GiveBackTask.AuditTasks(null);
            Assert.Equal(new[] { GiveBackTask.DeleteArguments(name) }, _calls);
        }

        [Fact]
        public void AKeyboardsTask_FromASharedFolder_IsMadeAgainWithWindowsToolsAlone()
        {
            string name = GiveBackTask.NameOf(Bind);
            GiveBackTask.OutputSeam = args => args.Contains("/FO CSV", System.StringComparison.Ordinal)
                ? "\"\\" + name + "\",\"N/A\",\"Ready\"\r\n"
                : Definition(@"C:\Users\someone\Downloads\PadForge.exe", GiveBackRun.PackageSwitch, Bind, name);
            GiveBackTask.AuditTasks(null);
            string create = Assert.Single(_calls);
            Assert.StartsWith($"/Create /TN \"{name}\" /XML", create, System.StringComparison.Ordinal);
            string xml = _created[create];
            Assert.DoesNotContain("PadForge.exe", xml, System.StringComparison.Ordinal);
            Assert.Contains("pnputil.exe", xml, System.StringComparison.Ordinal);
            // No wait on a pipe this app does not hold.
            Assert.DoesNotContain("RegistrationTrigger", xml, System.StringComparison.Ordinal);
        }

        [Fact]
        public void TheMove_PointsATaskAtTheNewExe()
        {
            string name = GiveBackTask.NameOf(Bind);
            GiveBackTask.MayRunExeSeam = exe => exe == @"C:\Program Files\PadForge\PadForge.exe";
            GiveBackTask.OutputSeam = args => args.Contains("/FO CSV", System.StringComparison.Ordinal)
                ? "\"\\" + name + "\",\"N/A\",\"Ready\"\r\n"
                : Definition(@"C:\Users\someone\Downloads\PadForge.exe", GiveBackRun.PackageSwitch, Bind, name);
            GiveBackTask.Repoint(@"C:\Program Files\PadForge\PadForge.exe", null);
            string xml = _created[Assert.Single(_calls)];
            Assert.Contains(@"&quot;C:\Program Files\PadForge\PadForge.exe&quot; --give-back-package", xml, System.StringComparison.Ordinal);
            Assert.DoesNotContain("Downloads", xml, System.StringComparison.Ordinal);
        }

        [Fact]
        public void ATaskWhoseNameIsNotItsIds_IsLeftAlone()
        {
            GiveBackTask.OutputSeam = args => args.Contains("/FO CSV", System.StringComparison.Ordinal)
                ? "\"\\PadForge Give Back 0011223344556677\",\"N/A\",\"Ready\"\r\n"
                : Definition(@"C:\Users\someone\Downloads\PadForge.exe", GiveBackRun.PackageSwitch, Bind, "PadForge Give Back 0011223344556677");
            GiveBackTask.AuditTasks(null);
            Assert.Empty(_calls);
        }
    }
}

namespace PadForge.Tests
{
    /// <summary>A vendor library's path is read from the machine's own
    /// registry and environment, never the user's (#597).</summary>
    [Collection("SettingsManagerStatics")]
    public class MachineEnvironmentTests : System.IDisposable
    {
        public MachineEnvironmentTests()
            => PadForge.Engine.Common.MachineEnvironment.GetSeam = name => name switch
            {
                "FDSDK" => @"%ProgramFiles%\Force Dimension\sdk-3.17.7",
                "ProgramFiles" => @"C:\Program Files",
                _ => null,
            };

        public void Dispose() => PadForge.Engine.Common.MachineEnvironment.GetSeam = null;

        [Fact]
        public void AVariable_IsFilledInFromTheMachinesValues()
        {
            Assert.Equal(@"C:\Program Files\Force Dimension\sdk-3.17.7\bin",
                PadForge.Engine.Common.MachineEnvironment.Expand(@"%FDSDK%\bin"));
            // A name the machine does not have stays as written.
            Assert.Equal(@"%USERPROFILE%\x", PadForge.Engine.Common.MachineEnvironment.Expand(@"%USERPROFILE%\x"));
            Assert.Equal("50% off", PadForge.Engine.Common.MachineEnvironment.Expand("50% off"));
            Assert.Null(PadForge.Engine.Common.MachineEnvironment.Expand(null));
        }

        [Fact]
        public void TheVendorCatalogs_ReadNoUserClassAndNoUserVariable()
        {
            foreach (string file in new[] { "LogitechGKeyCatalog.cs", "LogitechServerCatalog.cs" })
            {
                string source = AuditDelta20261002Tests.RepoText("PadForge.Engine", "Common", "Logitech", file);
                Assert.DoesNotContain("Registry.ClassesRoot", source, System.StringComparison.Ordinal);
                Assert.DoesNotContain("Registry.CurrentUser", source, System.StringComparison.Ordinal);
                Assert.DoesNotContain("Environment.ExpandEnvironmentVariables", source, System.StringComparison.Ordinal);
            }
            string falcon = AuditDelta20261002Tests.RepoText("PadForge.Engine", "Common", "ForceDimension", "ForceDimensionCatalog.cs");
            Assert.DoesNotContain("Environment.GetEnvironmentVariable(\"FDSDK\")", falcon, System.StringComparison.Ordinal);
            Assert.Contains("MachineEnvironment.Get(\"FDSDK\")", falcon, System.StringComparison.Ordinal);
        }
    }
}
