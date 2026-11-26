using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using PadForge.Common;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>The handover taker's test: it runs only in the test host the
    /// handover proof starts, which sets its variables.</summary>
    public sealed class HandoverTakerFactAttribute : FactAttribute
    {
        public HandoverTakerFactAttribute()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(ProtectedStartTests.HandoverNamespaceVariable)))
                Skip = "Run only by TheHandover_WithRights_SetsTheEventItsStartMadeInTheNamespace, in the test host it starts.";
        }
    }

    /// <summary>
    /// A start with rights unpacks its native libraries only into a folder no
    /// program without rights can change (#597): the decision, the rule that
    /// judges a folder, the way a folder that fails it is replaced, and the
    /// restarted copy's variables.
    /// </summary>
    public class ProtectedStartTests
    {
        private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private static byte[] Binary(string sddl)
        {
            var sd = new RawSecurityDescriptor(sddl);
            byte[] bytes = new byte[sd.BinaryLength];
            sd.GetBinaryForm(bytes, 0);
            return bytes;
        }

        private const string Natives = @"C:\ProgramData\PadForge\Natives";

        // ── The decision ──

        [Fact]
        public void AStartWithoutRights_GoesOn_WhereverItUnpacked()
            => Assert.Equal(ProtectedStart.Decision.GoOn,
                ProtectedStart.Decide(false, null, Natives, Same, () => throw new InvalidOperationException("not asked")));

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(@"C:\Users\someone\AppData\Local\Temp\.net")]
        [InlineData(@"C:\ProgramData\PadForge")]
        [InlineData(@"C:\ProgramData\PadForge\Natives\Other")]
        public void AStartWithRights_ThatUnpackedAnywhereElse_StartsAgain(string variable)
            => Assert.Equal(ProtectedStart.Decision.Restart, ProtectedStart.Decide(true, variable, Natives, Same, () => true));

        [Theory]
        [InlineData(@"C:\ProgramData\PadForge\Natives")]
        [InlineData(@"c:\programdata\padforge\natives\")]
        public void AStartWithRights_GoesOn_OnlyFromTheProtectedFolder_WhileItIsSound(string variable)
        {
            Assert.Equal(ProtectedStart.Decision.GoOn, ProtectedStart.Decide(true, variable, Natives, Same, () => true));
            // A variable the user's environment set to that folder before
            // PadForge made it sound is not taken on its word.
            Assert.Equal(ProtectedStart.Decision.Restart, ProtectedStart.Decide(true, variable, Natives, Same, () => false));
        }

        [Fact]
        public void AStartWithRights_StartsAgain_WhenTheRootCannotBeNamed()
            => Assert.Equal(ProtectedStart.Decision.Restart, ProtectedStart.Decide(true, Natives, string.Empty, Same, () => true));

        // ── Where the root is ──

        [Theory]
        [InlineData(@"C:\WINDOWS", @"%SystemDrive%\ProgramData", @"C:\ProgramData\PadForge")]
        [InlineData(@"D:\Windows", @"%SYSTEMDRIVE%\ProgramData", @"D:\ProgramData\PadForge")]
        [InlineData(@"C:\WINDOWS", @"E:\Shared\ProgramData\", @"E:\Shared\ProgramData\PadForge")]
        [InlineData(@"C:\WINDOWS", null, @"C:\ProgramData\PadForge")]
        // A value that names a variable only the process's environment holds
        // would let that environment choose the root.
        [InlineData(@"C:\WINDOWS", @"%USERPROFILE%\ProgramData", @"C:\ProgramData\PadForge")]
        [InlineData(@"C:\WINDOWS", @"ProgramData", @"C:\ProgramData\PadForge")]
        public void TheRoot_ComesFromWindowsOwnRecord_WithNoVariableOfTheProcess(string windows, string value, string expected)
            => Assert.Equal(expected, ProtectedFolder.RootUnder(windows, value));

        [Fact]
        public void TheRoot_IsNothing_WithoutWindowsFolder()
            => Assert.Equal(string.Empty, ProtectedFolder.RootUnder(string.Empty, @"%SystemDrive%\ProgramData"));

        [Fact]
        public void TheTestHost_HasNoRoot()
        {
            Assert.True(ProtectedFolder.OffLimitsForTest);
            Assert.Null(ProtectedFolder.RootSeam);
            Assert.Equal(string.Empty, ProtectedFolder.Root);
            Assert.Equal(string.Empty, ProtectedFolder.Natives);
            Assert.Equal(string.Empty, ProtectedFolder.Updates);
            Assert.Equal(string.Empty, ProtectedFolder.Work);
            Assert.False(ProtectedFolder.MakeNativesSound());
        }

        /// <summary>The root a start with rights uses on this PC, read the way
        /// it reads it: Windows' own record of %ProgramData%, filled in from
        /// Windows' folder. Read only.</summary>
        [Fact]
        public void TheRoot_OnThisPc_IsProgramDatasPadForge_InASoundParent()
        {
            string value = System32Calls.MachineText(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList", "ProgramData");
            Assert.False(string.IsNullOrEmpty(value));
            string root = ProtectedFolder.RootUnder(System32Calls.WindowsFolder, value);
            Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PadForge"), root,
                StringComparer.OrdinalIgnoreCase);
            Assert.Equal(ProtectedFolder.Verdict.Sound, ProtectedFolder.JudgeParent(root));
        }

        [Fact]
        public void WindowsFolder_IsReadFromTheKernelsPage_AndMatchesWindows()
        {
            Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\'), System32Calls.WindowsFolder,
                StringComparer.OrdinalIgnoreCase);
            Assert.True(System32Calls.Ready);
        }

        // ── The rule ──

        [Fact]
        public void TheFoldersOwnList_IsSound()
            => Assert.Equal(ProtectedFolder.Verdict.Sound, ProtectedFolder.Judge(Binary(ProtectedFolder.Sddl), false));

        [Theory]
        // Owned by SYSTEM.
        [InlineData("O:SYD:P(A;OICI;FA;;;BA)(A;OICI;FA;;;SY)", nameof(ProtectedFolder.Verdict.Sound))]
        // Users and Everyone may read and run.
        [InlineData("O:BAD:P(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)(A;OICI;GRGX;;;WD)", nameof(ProtectedFolder.Verdict.Sound))]
        // CREATOR OWNER's entry goes to whoever makes a file there.
        [InlineData("O:BAD:P(A;OICI;FA;;;BA)(A;OICIIO;FA;;;CO)", nameof(ProtectedFolder.Verdict.Sound))]
        // A deny entry takes nothing away from the rule.
        [InlineData("O:BAD:P(D;OICI;FA;;;BU)(A;OICI;FA;;;BA)", nameof(ProtectedFolder.Verdict.Sound))]
        // Owned by a user, who can always change the list.
        [InlineData("O:S-1-5-21-1-2-3-1001D:P(A;OICI;FA;;;BA)", nameof(ProtectedFolder.Verdict.OtherOwner))]
        [InlineData("O:BUD:P(A;OICI;FA;;;BA)", nameof(ProtectedFolder.Verdict.OtherOwner))]
        // No list at all: everyone may do everything.
        [InlineData("O:BA", nameof(ProtectedFolder.Verdict.NoList))]
        // What C:\ hands every folder made under it: Authenticated Users
        // modify, and modify inherited by what is made inside.
        [InlineData("O:BAD:(A;OICI;FA;;;BA)(A;;0x1301bf;;;AU)(A;OICIIO;SDGXGWGR;;;AU)", nameof(ProtectedFolder.Verdict.OthersMayChange))]
        [InlineData("O:BAD:(A;OICI;FA;;;BA)(A;OICIIO;GW;;;AU)", nameof(ProtectedFolder.Verdict.OthersMayChange))]
        // A user may add a file, add a folder, delete, or change the list.
        [InlineData("O:BAD:(A;OICI;FA;;;BA)(A;;0x2;;;BU)", nameof(ProtectedFolder.Verdict.OthersMayChange))]
        [InlineData("O:BAD:(A;OICI;FA;;;BA)(A;;0x4;;;BU)", nameof(ProtectedFolder.Verdict.OthersMayChange))]
        [InlineData("O:BAD:(A;OICI;FA;;;BA)(A;;SD;;;BU)", nameof(ProtectedFolder.Verdict.OthersMayChange))]
        [InlineData("O:BAD:(A;OICI;FA;;;BA)(A;;WD;;;BU)", nameof(ProtectedFolder.Verdict.OthersMayChange))]
        [InlineData("O:BAD:(A;OICI;FA;;;BA)(A;;WO;;;BU)", nameof(ProtectedFolder.Verdict.OthersMayChange))]
        [InlineData("O:BAD:(A;OICI;FA;;;BA)(A;;0x40;;;BU)", nameof(ProtectedFolder.Verdict.OthersMayChange))]
        [InlineData("O:BAD:(A;OICI;FA;;;BA)(A;;GA;;;WD)", nameof(ProtectedFolder.Verdict.OthersMayChange))]
        public void AFolder_IsSound_OnlyWhenNoOtherAccountMayChangeIt(string sddl, string expected)
            => Assert.Equal(Enum.Parse<ProtectedFolder.Verdict>(expected), ProtectedFolder.Judge(Binary(sddl), false));

        /// <summary>%ProgramData% as Windows sets it up, read on this PC: Users
        /// may make folders and files there, and no account but Administrators
        /// and SYSTEM may delete one, so the root cannot be swapped.</summary>
        [Fact]
        public void ProgramDataAsWindowsSetsItUp_HoldsTheRoot()
            => Assert.Equal(ProtectedFolder.Verdict.Sound, ProtectedFolder.JudgeParent(Binary(
                "O:SYG:SYD:PAI(A;OICIIO;GA;;;CO)(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)(A;CI;DCLCRPCR;;;BU)")));

        [Theory]
        // Users may delete what is in it.
        [InlineData("O:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;;0x40;;;BU)", nameof(ProtectedFolder.Verdict.OthersMayChange))]
        // Or change who may.
        [InlineData("O:SYD:P(A;OICI;FA;;;SY)(A;;WD;;;AU)", nameof(ProtectedFolder.Verdict.OthersMayChange))]
        [InlineData("O:SYD:P(A;OICI;FA;;;SY)(A;;GA;;;WD)", nameof(ProtectedFolder.Verdict.OthersMayChange))]
        // A user owns it.
        [InlineData("O:BUD:P(A;OICI;FA;;;SY)", nameof(ProtectedFolder.Verdict.OtherOwner))]
        // What only folders made inside inherit is not the parent's own.
        [InlineData("O:SYD:P(A;OICI;FA;;;SY)(A;OICIIO;GA;;;AU)", nameof(ProtectedFolder.Verdict.Sound))]
        // The root of drive C: as Windows sets it up.
        [InlineData("O:S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464D:PAI(A;;LC;;;AU)(A;OICIIO;SDGXGWGR;;;AU)(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)", nameof(ProtectedFolder.Verdict.Sound))]
        public void AParent_HoldsTheRoot_OnlyWhenNoOtherAccountMayReplaceIt(string sddl, string expected)
            => Assert.Equal(Enum.Parse<ProtectedFolder.Verdict>(expected), ProtectedFolder.JudgeParent(Binary(sddl)));

        /// <summary>C:\PadForge on this PC, read when this rule was written:
        /// owned by the user, and the entry C:\ hands down gives Authenticated Users
        /// modify over it and all it holds.</summary>
        [Fact]
        public void TheExeFolder_AsItStoodOnThisPc_IsNotSound()
            => Assert.Equal(ProtectedFolder.Verdict.OtherOwner, ProtectedFolder.Judge(Binary(
                "O:S-1-5-21-3155206576-1441097098-1295047565-1001G:S-1-5-21-3155206576-1441097098-1295047565-513D:AI(A;OICIID;FA;;;BA)(A;OICIID;FA;;;SY)(A;OICIID;0x1200a9;;;BU)(A;ID;0x1301bf;;;AU)(A;OICIIOID;SDGXGWGR;;;AU)"), true));

        [Fact]
        public void TrustedInstaller_OwnsASoundFolder_OnlyWhereItIsTrustedAsOwner()
        {
            byte[] windows = Binary("O:" + ProtectedFolder.TrustedInstallerSid + "D:P(A;OICI;FA;;;BA)(A;OICI;FA;;;SY)(A;OICI;FA;;;"
                + ProtectedFolder.TrustedInstallerSid + ")");
            Assert.Equal(ProtectedFolder.Verdict.OtherOwner, ProtectedFolder.Judge(windows, false));
            Assert.Equal(ProtectedFolder.Verdict.Sound, ProtectedFolder.Judge(windows, true));
        }

        [Fact]
        public void ALinkOrAFile_IsNeverSound()
        {
            byte[] sound = Binary(ProtectedFolder.Sddl);
            Assert.Equal(ProtectedFolder.Verdict.Sound, ProtectedFolder.Judge(sound, System32Calls.AttributeDirectory, false));
            Assert.Equal(ProtectedFolder.Verdict.NotAFolder,
                ProtectedFolder.Judge(sound, System32Calls.AttributeDirectory | System32Calls.AttributeReparsePoint, false));
            Assert.Equal(ProtectedFolder.Verdict.NotAFolder, ProtectedFolder.Judge(sound, 0x20, false));
        }

        [Fact]
        public void AGarbledDescriptor_IsUnreadable()
            => Assert.Equal(ProtectedFolder.Verdict.Unreadable, ProtectedFolder.Judge(new byte[] { 1, 0, 4 }, false));

        [Fact]
        public void TheReadOnlyRights_AreTheReadAndExecuteSet()
        {
            // FILE_GENERIC_READ | FILE_GENERIC_EXECUTE, and GENERIC_READ | GENERIC_EXECUTE.
            Assert.Equal(0x1200A9, PadForge.Engine.Common.ChangeRule.ReadOnlyRights & 0x1FFFFF);
            Assert.Equal(unchecked((int)0xA0000000), PadForge.Engine.Common.ChangeRule.ReadOnlyRights & unchecked((int)0xF0000000));
        }

        [Fact]
        public void ReadingARealFolder_JudgesTheFolderItself()
        {
            string dir = Path.Combine(UpdateService.TestScratchRoot, "protected-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                // Made by this test host in its temp folder, which its user
                // owns: never sound.
                var verdict = ProtectedFolder.Judge(dir);
                Assert.Contains(verdict, new[] { ProtectedFolder.Verdict.OtherOwner, ProtectedFolder.Verdict.OthersMayChange });
                string file = Path.Combine(dir, "a.txt");
                File.WriteAllText(file, "x");
                Assert.Equal(ProtectedFolder.Verdict.NotAFolder, ProtectedFolder.Judge(file));
                Assert.Equal(ProtectedFolder.Verdict.NotThere, ProtectedFolder.Judge(Path.Combine(dir, "missing")));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        // ── Making a folder sound ──

        private sealed class FakeDisk
        {
            public readonly Dictionary<string, ProtectedFolder.Verdict> State = new(StringComparer.OrdinalIgnoreCase);
            public readonly List<string> Steps = new();
            public bool RenameWorks = true;
            public bool CreateWorks = true;

            public ProtectedFolder.FolderOps Ops() => new()
            {
                Judge = path => State.TryGetValue(path, out var v) ? v : ProtectedFolder.Verdict.NotThere,
                Rename = (from, to) =>
                {
                    Steps.Add("rename " + from + " -> " + to);
                    if (!RenameWorks) return false;
                    State[to] = State[from];
                    State.Remove(from);
                    return true;
                },
                Create = (path, sddl) =>
                {
                    Steps.Add("create " + path + " " + sddl);
                    if (!CreateWorks || State.ContainsKey(path)) return false;
                    State[path] = ProtectedFolder.Verdict.Sound;
                    return true;
                },
                Stamp = () => "1a-2b",
            };
        }

        [Fact]
        public void ASoundFolder_IsLeftAsItIs()
        {
            var disk = new FakeDisk();
            disk.State[@"C:\PD\PadForge"] = ProtectedFolder.Verdict.Sound;
            Assert.True(ProtectedFolder.MakeSound(@"C:\PD\PadForge", disk.Ops()));
            Assert.Empty(disk.Steps);
        }

        [Fact]
        public void AMissingFolder_IsMadeWithTheProtectedList()
        {
            var disk = new FakeDisk();
            Assert.True(ProtectedFolder.MakeSound(@"C:\PD\PadForge", disk.Ops()));
            Assert.Equal(new[] { @"create C:\PD\PadForge " + ProtectedFolder.Sddl }, disk.Steps);
        }

        [Theory]
        [InlineData(nameof(ProtectedFolder.Verdict.OtherOwner))]
        [InlineData(nameof(ProtectedFolder.Verdict.OthersMayChange))]
        [InlineData(nameof(ProtectedFolder.Verdict.NoList))]
        [InlineData(nameof(ProtectedFolder.Verdict.NotAFolder))]
        [InlineData(nameof(ProtectedFolder.Verdict.Unreadable))]
        public void AFolderAnotherAccountMade_IsMovedAsideUnopened_AndANewOneMade(string foundName)
        {
            var found = Enum.Parse<ProtectedFolder.Verdict>(foundName);
            var disk = new FakeDisk();
            disk.State[@"C:\PD\PadForge"] = found;
            Assert.True(ProtectedFolder.MakeSound(@"C:\PD\PadForge", disk.Ops()));
            Assert.Equal(new[]
            {
                @"rename C:\PD\PadForge -> C:\PD\PadForge.untrusted-1a-2b-0",
                @"create C:\PD\PadForge " + ProtectedFolder.Sddl,
            }, disk.Steps);
            Assert.Equal(found, disk.State[@"C:\PD\PadForge.untrusted-1a-2b-0"]);
        }

        [Fact]
        public void AFolderThatCannotBeMoved_IsNeverUsed()
        {
            var disk = new FakeDisk { RenameWorks = false };
            disk.State[@"C:\PD\PadForge"] = ProtectedFolder.Verdict.OthersMayChange;
            Assert.False(ProtectedFolder.MakeSound(@"C:\PD\PadForge", disk.Ops()));
            Assert.Equal(4, disk.Steps.Count);
            Assert.All(disk.Steps, s => Assert.StartsWith("rename ", s, StringComparison.Ordinal));
        }

        [Fact]
        public void AFolderMadeAgainInTheMeantime_IsJudgedBeforeItIsTaken()
        {
            // Another account makes the name again between the move and the
            // make, once. The second pass moves that one aside too.
            var disk = new FakeDisk();
            disk.State[@"C:\PD\PadForge"] = ProtectedFolder.Verdict.OtherOwner;
            var ops = disk.Ops();
            var create = ops.Create;
            bool raced = false;
            ops.Create = (path, sddl) =>
            {
                if (!raced)
                {
                    raced = true;
                    disk.State[path] = ProtectedFolder.Verdict.OtherOwner;
                }
                return create(path, sddl);
            };
            Assert.True(ProtectedFolder.MakeSound(@"C:\PD\PadForge", ops));
            Assert.Equal(ProtectedFolder.Verdict.Sound, disk.State[@"C:\PD\PadForge"]);
            Assert.Equal(2, disk.Steps.Count(s => s.StartsWith("rename ", StringComparison.Ordinal)));
        }

        [Fact]
        public void AFolderAnotherStartWithRightsMade_IsTaken()
        {
            var disk = new FakeDisk();
            var ops = disk.Ops();
            ops.Create = (path, sddl) =>
            {
                disk.Steps.Add("create " + path);
                disk.State[path] = ProtectedFolder.Verdict.Sound;
                return false;
            };
            Assert.True(ProtectedFolder.MakeSound(@"C:\PD\PadForge", ops));
        }

        [Fact]
        public void AFolderThatCannotBeMade_IsNeverUsed()
        {
            var disk = new FakeDisk { CreateWorks = false };
            Assert.False(ProtectedFolder.MakeSound(@"C:\PD\PadForge", disk.Ops()));
        }

        // ── The restarted copy's variables ──

        [Fact]
        public void TheRestartedCopy_GetsTheProtectedFolder_AndTheHandover_InTheirPlaces()
        {
            var variables = new[]
            {
                "=C:=C:\\Users\\someone",
                "ALLUSERSPROFILE=C:\\ProgramData",
                "dotnet_bundle_extract_base_dir=C:\\Users\\someone\\planted",
                "Path=C:\\WINDOWS\\system32",
                "PADFORGE_HANDOVER_EVENT=dead",
                "TEMP=C:\\Users\\someone\\AppData\\Local\\Temp",
            };
            // The machine's block names the system's variables. The user's
            // own Path and a variable on no list are not handed on, nor the
            // drive lines, which a copy started in the exe's own folder
            // does not read.
            var machine = new[] { "ALLUSERSPROFILE=C:\\ProgramData", "Path=C:\\WINDOWS\\system32" };
            string block = ProtectedStart.CopyBlock(machine, variables.Concat(new[] { "Path=C:\\Users\\someone\\bin", "VR_OVERRIDE=x" }).ToArray(), Natives, "1f4");
            Assert.EndsWith("\0\0", block, StringComparison.Ordinal);
            var lines = block.TrimEnd('\0').Split('\0');
            Assert.Equal(new[]
            {
                "ALLUSERSPROFILE=C:\\ProgramData",
                "DOTNET_BUNDLE_EXTRACT_BASE_DIR=" + Natives,
                "PADFORGE_HANDOVER_EVENT=1f4",
                "Path=C:\\WINDOWS\\system32",
                "TEMP=C:\\Users\\someone\\AppData\\Local\\Temp",
            }, lines);
        }

        [Theory]
        [InlineData("Path=C:\\x", "Path")]
        [InlineData("=C:=C:\\x", "=C:")]
        [InlineData("EMPTY=", "EMPTY")]
        [InlineData("NOEQUALS", "NOEQUALS")]
        public void AVariablesName_RunsToItsEqualsSign(string line, string name)
            => Assert.Equal(name, ProtectedStart.NameOf(line));

        [Theory]
        [InlineData("1f4", 0x1f4UL, true)]
        [InlineData("ABCdef", 0xabcdefUL, true)]
        [InlineData("", 0UL, false)]
        [InlineData("12g", 0UL, false)]
        [InlineData("11112222333344445", 0UL, false)]
        public void TheHandoverValue_IsReadAsHex(string text, ulong value, bool ok)
        {
            Assert.Equal(ok, ProtectedStart.TryParseHex(text, out ulong got));
            if (ok) Assert.Equal(value, got);
        }

        [Theory]
        [InlineData(0UL, "0")]
        [InlineData(0x1f4UL, "1f4")]
        [InlineData(ulong.MaxValue, "ffffffffffffffff")]
        public void Hex_IsWrittenWithoutTheUsersCulture(ulong value, string text)
        {
            Assert.Equal(text, ProtectedFolder.Hex(value));
            Assert.True(ProtectedStart.TryParseHex(text, out ulong back));
            Assert.Equal(value, back);
        }

        // ── Main ──

        /// <summary>The step runs before anything else in Main, the copy that
        /// becomes the app lets the start that restarted it go, and the step
        /// makes no DllImport call of its own.</summary>
        [Fact]
        public void TheStep_RunsFirst_AndCallsWindowsOnlyThroughSystem32()
        {
            string program = AuditDelta20261002Tests.RepoText("PadForge.App", "Program.cs");
            Assert.StartsWith("ProtectedStart.Run();", FenceSource.FirstStatement(program, "public static void Main(string[] args)"), StringComparison.Ordinal);
            string app = AuditDelta20261002Tests.RepoText("PadForge.App", "App.xaml.cs");
            int lockTaken = app.IndexOf("InstanceSignal.Open();", StringComparison.Ordinal);
            int handOver = app.IndexOf("Services.ProtectedStart.HandOver();", StringComparison.Ordinal);
            // After the update helper's own exit, and once this copy holds
            // the lock, so the helper's starter still reads its exit code.
            Assert.True(app.IndexOf("Services.UpdateService.TryRunApplyMode(e.Args", StringComparison.Ordinal) < lockTaken);
            Assert.True(lockTaken > 0 && handOver > lockTaken);
            Assert.DoesNotContain("HandOver", program, StringComparison.Ordinal);
            foreach (string file in new[]
            {
                AuditDelta20261002Tests.RepoText("PadForge.App", "Services", "ProtectedStart.cs"),
                AuditDelta20261002Tests.RepoText("PadForge.App", "Common", "ProtectedFolder.cs"),
                AuditDelta20261002Tests.RepoText("PadForge.App", "Common", "System32Calls.cs"),
                AuditDelta20261002Tests.RepoText("PadForge.App", "Services", "RightsVariables.cs"),
                AuditDelta20261002Tests.RepoText("PadForge.App", "Services", "UserVariables.cs"),
                AuditDelta20261002Tests.RepoText("PadForge.Engine", "Common", "ChangeRule.cs"),
            })
            {
                string code = string.Join("\n", file.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
                Assert.DoesNotContain("DllImport(", code, StringComparison.Ordinal);
                Assert.DoesNotContain("LibraryImport(", code, StringComparison.Ordinal);
                Assert.DoesNotContain("Environment.", code, StringComparison.Ordinal);
                Assert.DoesNotContain(" Path.", code, StringComparison.Ordinal);
                Assert.DoesNotContain("(Path.", code, StringComparison.Ordinal);
                Assert.DoesNotContain("ToString(\"", code, StringComparison.Ordinal);
                Assert.DoesNotContain("throw ", code, StringComparison.Ordinal);
                Assert.DoesNotContain("Guid.", code, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void TheBuild_TurnsOffStartupHooks_AndEventSource()
        {
            string project = AuditDelta20261002Tests.RepoText("PadForge.App", "PadForge.App.csproj");
            Assert.Contains("<StartupHookSupport>false</StartupHookSupport>", project, StringComparison.Ordinal);
            Assert.Contains("<EventSourceSupport>false</EventSourceSupport>", project, StringComparison.Ordinal);
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateMutexW(IntPtr attributes, bool owned, string name);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetHandleInformation(IntPtr handle, out uint flags);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        /// <summary>Every program of the user can set the handover variable
        /// for a start through Windows' prompt. A value it wrote, the number
        /// of a handle this process holds, sets and closes nothing: the
        /// handover is an event the start names in the Administrators
        /// namespace, which only a token with rights opens, and a test host
        /// without rights opens none.</summary>
        [Fact]
        public void AHandoverValueAProgramWrote_ClosesNoHandleOfThisProcess()
        {
            string ns = "PadForge.Tests.Handover." + Guid.NewGuid().ToString("N");
            IntPtr mutex = CreateMutexW(IntPtr.Zero, false, null);
            Assert.NotEqual(IntPtr.Zero, mutex);
            bool open = true;
            try
            {
                ProtectedStart.TakeHandover(ProtectedFolder.Hex(unchecked((ulong)(long)mutex)), ns);
                ProtectedStart.HandOver();
                open = GetHandleInformation(mutex, out _);
                Assert.True(open, "the handover closed the handle its variable named");

                // A name of the handover's shape opens nothing without rights.
                ProtectedStart.TakeHandover("Handover-1f-2e", ns);
                ProtectedStart.HandOver();
                Assert.True(GetHandleInformation(mutex, out _));
            }
            finally
            {
                if (open) CloseHandle(mutex);
            }
            Assert.True(ProtectedStart.IsHandoverName("Handover-1f-2e"));
            Assert.True(ProtectedStart.IsHandoverName(ProtectedStart.HandoverName()));
            foreach (string value in new[] { "1a4", "Handover-", "Handover-1f", "Handover-1f-", "Handover--2e", "Handover-1f-2e-3",
                         "Handover-zz-1", "Handover-1f-2e\\..\\x", "handover-1f-2e", "Handover-" + new string('f', 17) + "-1" })
                Assert.False(ProtectedStart.IsHandoverName(value), value);
        }

        /// <summary>The variable that makes the copy's side below run, set
        /// only by the test that starts it: the namespace to take the
        /// handover from.</summary>
        internal const string HandoverNamespaceVariable = "PADFORGE_TEST_HANDOVER_NS";

        /// <summary>With rights, the restarting start's steps in two
        /// processes, as a start and its copy take them. This one makes the
        /// event under its name in an Administrators namespace of the test's
        /// own and holds both, as StartCopy does. A test host it starts, with
        /// the name in the handover variable, takes the handover and hands
        /// over, as the copy's Run and App.OnStartup do, and that sets the
        /// event. The take runs in another process because Windows lets one
        /// process open a private namespace under one alias once: a second
        /// open answers ERROR_DUP_NAME, and TakeHandover then opens
        /// nothing.</summary>
        [ElevatedFact]
        public void TheHandover_WithRights_SetsTheEventItsStartMadeInTheNamespace()
        {
            string ns = "PadForge.Tests.Handover." + Guid.NewGuid().ToString("N");
            IntPtr space = System32Calls.OpenAdministratorsNamespace(ns, create: true, out IntPtr boundary);
            Assert.NotEqual(IntPtr.Zero, space);
            string name = ProtectedStart.HandoverName();
            IntPtr made = System32Calls.NewNamedEvent(ns + "\\" + name);
            try
            {
                Assert.NotEqual(IntPtr.Zero, made);
                Assert.Equal(0x102u /* WAIT_TIMEOUT */, WaitForSingleObject(made, 0));

                var psi = ElevatedProofTests.InnerTestRun("FullyQualifiedName~ProtectedStartTests.TakesTheHandoverForAnotherProcess");
                psi.Environment[ProtectedStart.HandoverVariable] = name;
                psi.Environment[HandoverNamespaceVariable] = ns;
                var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
                using var copy = new System.Diagnostics.Process { StartInfo = psi };
                copy.OutputDataReceived += (_, e) => { if (e.Data != null) lines.Enqueue(e.Data); };
                copy.ErrorDataReceived += (_, e) => { if (e.Data != null) lines.Enqueue("err " + e.Data); };
                copy.Start();
                copy.BeginOutputReadLine();
                copy.BeginErrorReadLine();
                if (!copy.WaitForExit(120000))
                {
                    try { copy.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                    copy.WaitForExit(10000);
                    Assert.Fail("the copy's side did not end: " + string.Join(" | ", lines));
                }
                copy.WaitForExit();
                string said = "exit " + copy.ExitCode + ": " + string.Join(" | ", lines);
                Assert.True(copy.ExitCode == 0, "the copy's side failed, " + said);
                Assert.True(lines.Any(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"Passed:\s+1,")), "the copy's side did not run, " + said);

                Assert.True(WaitForSingleObject(made, 0) == 0 /* WAIT_OBJECT_0 */, "the copy's handover did not set the event, " + said);
                // The same name a second time is refused: it is taken.
                Assert.Equal(IntPtr.Zero, System32Calls.NewNamedEvent(ns + "\\" + name));
            }
            finally
            {
                System32Calls.Close(made);
                System32Calls.CloseNamespace(space, boundary);
            }
        }

        /// <summary>The copy's side of the test above: the take from the
        /// handover variable, as Run makes it, and the handover. Runs only
        /// when that test starts it.</summary>
        [HandoverTakerFact]
        public void TakesTheHandoverForAnotherProcess()
        {
            string ns = Environment.GetEnvironmentVariable(HandoverNamespaceVariable);
            Assert.True(ProtectedStart.IsHandoverName(System32Calls.GetVariable(ProtectedStart.HandoverVariable)));
            ProtectedStart.TakeHandover(System32Calls.GetVariable(ProtectedStart.HandoverVariable), ns);
            Assert.Null(System32Calls.GetVariable(ProtectedStart.HandoverVariable));
            ProtectedStart.HandOver();
        }
    }
}
