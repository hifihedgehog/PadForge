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
            string protect = FenceSource.Body(source, "internal static bool Protect(string folder, IReadOnlyList<Item> items)");
            Assert.StartsWith("if (OffLimitsForTest) return false;", FenceSource.FirstStatement(source, "internal static bool Protect(string folder, IReadOnlyList<Item> items)"), StringComparison.Ordinal);
            Assert.Contains("using (Privileges.Hold(\"SeTakeOwnershipPrivilege\", \"SeRestorePrivilege\"))", protect, StringComparison.Ordinal);
            Assert.Contains("own.SetSecurityDescriptorSddlForm(ProtectedFolder.Sddl);", protect, StringComparison.Ordinal);
            Assert.Contains("inherit.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);", protect, StringComparison.Ordinal);
            Assert.Contains("return !OthersMayChange(folder, FilesOf(folder, items));", protect, StringComparison.Ordinal);
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
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(shell, @"\bProcess\.Start\("));
            Assert.Contains("internal static Process StartOwnHelper(ProcessStartInfo psi)\r\n            => OffLimitsForTest ? null : Process.Start(psi);", shell.Replace("\r\n", "\n").Replace("\n", "\r\n"), StringComparison.Ordinal);
            string macros = AuditDelta20261002Tests.RepoText("PadForge.App", "Common", "Input", "InputManager.Step4b.EvaluateMacros.cs");
            Assert.Contains("if (!PadForge.Common.ShellLaunch.AsTheUser(psi, out string why))", macros, StringComparison.Ordinal);
            // Only PadForge's own helpers start with its rights.
            var helpers = new List<string>();
            foreach (string project in FenceSource.Projects)
                foreach (string path in OffLimitsSource.Sources(project))
                    if (System.IO.File.ReadAllText(path).Contains("ShellLaunch.StartOwnHelper(", StringComparison.Ordinal))
                        helpers.Add(Path.GetFileName(path));
            Assert.Equal(new[] { "App.xaml.cs", "DsxStandIn.cs" }, helpers.OrderBy(h => h, StringComparer.Ordinal).ToArray());
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
            Assert.False(StartupHelper.AuditTask());
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
