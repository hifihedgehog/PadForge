using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using PadForge.Common;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Start at Login task's run (#597): a SYSTEM task that starts the
    /// app in the user's session with variables it builds itself, so no
    /// variable a program of the user sets reaches the app with rights.
    /// </summary>
    public class LogonStartTests
    {
        private const string Sid = "S-1-5-21-3155206576-1441097098-1295047565-1001";
        private const string Natives = @"C:\ProgramData\PadForge\Natives";

        private static readonly string[] System =
        {
            "ALLUSERSPROFILE=C:\\ProgramData", "ComSpec=C:\\WINDOWS\\system32\\cmd.exe", "NUMBER_OF_PROCESSORS=16", "OS=Windows_NT",
            "Path=C:\\WINDOWS\\system32;C:\\WINDOWS", "PROCESSOR_ARCHITECTURE=AMD64", "SystemRoot=C:\\WINDOWS",
            "TEMP=C:\\WINDOWS\\TEMP", "USERNAME=SYSTEM", "windir=C:\\WINDOWS",
        };

        /// <summary>What CreateEnvironmentBlock gives for a user whose
        /// HKCU\Environment a program filled: its own Path, a ComSpec of its
        /// own, and every variable that loads code into a .NET or SDL
        /// process.</summary>
        private static readonly string[] User =
        {
            "=C:=C:\\", "ALLUSERSPROFILE=C:\\ProgramData", "APPDATA=C:\\Users\\sonic\\AppData\\Roaming",
            "COMPlus_EnableDiagnostics=1", "ComSpec=C:\\Users\\sonic\\evil.exe", "COR_ENABLE_PROFILING=1",
            "COR_PROFILER_PATH=C:\\Users\\sonic\\p.dll", "CORECLR_ENABLE_PROFILING=1", "CORECLR_PROFILER=x",
            "CORECLR_PROFILER_PATH=C:\\Users\\sonic\\p.dll", "DOTNET_STARTUP_HOOKS=C:\\Users\\sonic\\h.dll",
            "DOTNET_BUNDLE_EXTRACT_BASE_DIR=C:\\Users\\sonic\\x", "LOCALAPPDATA=C:\\Users\\sonic\\AppData\\Local",
            "PADFORGE_DIAG=C:\\PadForge\\diag.log", "PADFORGE_HANDOVER_EVENT=1C", "PADFORGE_POLL_HZ=1000",
            "Path=C:\\Users\\sonic\\bin;C:\\WINDOWS\\system32", "SDL3_DYNAMIC_API=C:\\Users\\sonic\\sdl.dll",
            "TEMP=C:\\Users\\sonic\\AppData\\Local\\Temp", "TMP=C:\\Users\\sonic\\AppData\\Local\\Temp", "USERNAME=sonic",
            "USERPROFILE=C:\\Users\\sonic", "VR_OVERRIDE=C:\\Users\\sonic\\vr", "windir=C:\\Users\\sonic",
        };

        private static Dictionary<string, string> AsMap(IEnumerable<string> lines)
            => lines.ToDictionary(ProtectedStart.NameOf, l => l.Substring(ProtectedStart.NameOf(l).Length + 1), StringComparer.OrdinalIgnoreCase);

        [Fact]
        public void TheAppsVariables_TakeTheSystemsFromTheMachine_TheFoldersFromTheUser_AndNothingElse()
        {
            var built = RightsVariables.Build(System, User, Natives);
            var map = AsMap(built);
            // The machine's values, whatever the user set under the same name.
            Assert.Equal("C:\\WINDOWS\\system32;C:\\WINDOWS", map["Path"]);
            Assert.Equal("C:\\WINDOWS\\system32\\cmd.exe", map["ComSpec"]);
            Assert.Equal("C:\\WINDOWS", map["windir"]);
            // The user's own folders and name.
            Assert.Equal("C:\\Users\\sonic\\AppData\\Local\\Temp", map["TEMP"]);
            Assert.Equal("sonic", map["USERNAME"]);
            Assert.Equal("C:\\Users\\sonic", map["USERPROFILE"]);
            Assert.Equal("1000", map["PADFORGE_POLL_HZ"]);
            Assert.Equal("C:\\PadForge\\diag.log", map["PADFORGE_DIAG"]);
            // The unpack folder is the protected one, not the user's.
            Assert.Equal(Natives, map[ProtectedStart.BaseVariable]);
            // Nothing that loads code, and nothing not on a list.
            foreach (string name in map.Keys)
            {
                Assert.False(name.StartsWith("CORECLR_", StringComparison.OrdinalIgnoreCase), name);
                Assert.False(name.StartsWith("COR_", StringComparison.OrdinalIgnoreCase), name);
                Assert.False(name.StartsWith("COMPlus_", StringComparison.OrdinalIgnoreCase), name);
                Assert.False(name.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase) && name != ProtectedStart.BaseVariable, name);
                Assert.True(RightsVariables.SystemVariables.Contains(name, StringComparer.OrdinalIgnoreCase)
                            || RightsVariables.UserVariables.Contains(name, StringComparer.OrdinalIgnoreCase)
                            || RightsVariables.PadForgeVariables.Contains(name, StringComparer.OrdinalIgnoreCase)
                            || name == ProtectedStart.BaseVariable, name);
            }
            Assert.DoesNotContain("SDL3_DYNAMIC_API", map.Keys);
            Assert.DoesNotContain("VR_OVERRIDE", map.Keys);
            Assert.DoesNotContain("PADFORGE_HANDOVER_EVENT", map.Keys);
            Assert.DoesNotContain(built, l => l.StartsWith('='));
            // Each name once, in the order Windows keeps a block.
            Assert.Equal(map.Count, built.Count);
            for (int i = 1; i < built.Count; i++)
                Assert.True(ProtectedStart.CompareNames(ProtectedStart.NameOf(built[i - 1]), ProtectedStart.NameOf(built[i])) < 0);
            Assert.EndsWith("\0\0", RightsVariables.ToBlock(built), StringComparison.Ordinal);
        }

        /// <summary>Every PADFORGE_ variable the source reads is on the list
        /// the app gets, and the list names none the source does not
        /// read.</summary>
        [Fact]
        public void ThePadForgeVariables_AreTheOnesTheSourceReads()
        {
            var read = new SortedSet<string>(StringComparer.Ordinal);
            // The plugin kit's files are read by a plugin, which PadForge
            // starts without rights, and never compiled into PadForge.
            string kit = Path.Combine("PadForge.App", "Resources", "Plugins") + Path.DirectorySeparatorChar;
            foreach (string project in FenceSource.Projects)
                foreach (string path in OffLimitsSource.Sources(project).Where(p => !p.Contains(kit, StringComparison.OrdinalIgnoreCase)))
                    foreach (Match m in Regex.Matches(File.ReadAllText(path), "GetEnvironmentVariable\\(\"(PADFORGE_[A-Z0-9_]+)\""))
                        read.Add(m.Groups[1].Value);
            Assert.Equal(read.ToArray(), RightsVariables.PadForgeVariables.OrderBy(n => n, StringComparer.Ordinal).ToArray());
        }

        [Fact]
        public void TheSession_IsTheUsersActiveOne_ThenItsLatest()
        {
            var sessions = new[]
            {
                new LogonStart.SessionEntry(1, false, 100, Sid),
                new LogonStart.SessionEntry(2, false, 300, Sid),
                new LogonStart.SessionEntry(3, true, 50, "S-1-5-21-1-2-3-1002"),
            };
            Assert.Equal(2u, LogonStart.PickSession(sessions, Sid));
            Assert.Equal(1u, LogonStart.PickSession(new[]
            {
                new LogonStart.SessionEntry(1, true, 10, Sid),
                new LogonStart.SessionEntry(2, false, 300, Sid),
            }, Sid));
            Assert.Equal(3u, LogonStart.PickSession(sessions, "s-1-5-21-1-2-3-1002"));
            Assert.Null(LogonStart.PickSession(sessions, "S-1-5-21-9"));
            Assert.Null(LogonStart.PickSession(Array.Empty<LogonStart.SessionEntry>(), Sid));
        }

        [Fact]
        public void OnlyASidsForm_IsTakenFromTheCommandLine()
        {
            Assert.True(LogonStart.LooksLikeSid(Sid));
            Assert.False(LogonStart.LooksLikeSid(null));
            Assert.False(LogonStart.LooksLikeSid("S-1-"));
            Assert.False(LogonStart.LooksLikeSid("S-1-5-21-1 & calc"));
            Assert.False(LogonStart.LooksLikeSid("S-1-5-21-1-"));
            Assert.False(LogonStart.LooksLikeSid("DOMAIN\\sonic"));
            Assert.True(LogonStart.IsRun(new[] { LogonStart.Switch, Sid }));
            Assert.False(LogonStart.IsRun(new[] { LogonStart.Switch }));
            Assert.Equal(LogonStart.BadArguments, LogonStart.Run(new[] { LogonStart.Switch, "nobody" }));
        }

        /// <summary>The test host is not SYSTEM, so a launch starts nothing.</summary>
        [Fact]
        public void ALaunch_OutsideSystem_StartsNothing()
        {
            Assert.Equal(LogonStart.NotSystem, LogonStart.Launch(Sid, @"C:\nowhere\PadForge.exe", "\"C:\\nowhere\\PadForge.exe\"", @"C:\nowhere", Natives, out var report));
            Assert.Equal(0, report.ProcessId);
            string source = AuditDelta20261002Tests.RepoText("PadForge.App", "Services", "LogonStart.cs");
            string launch = FenceSource.Body(source, "internal static int Launch(string sid, string exe, string commandLine, string folder, string natives, out Report report)");
            Assert.True(launch.IndexOf("return NotSystem;", StringComparison.Ordinal) < launch.IndexOf("CreateProcessAsUserW(", StringComparison.Ordinal));
            // It leaves the task's job and runs at the normal priority.
            Assert.Contains("flags | CreateBreakawayFromJob", launch, StringComparison.Ordinal);
            Assert.Contains("CreateUnicodeEnvironment | NormalPriorityClass", launch, StringComparison.Ordinal);
            Assert.Contains("lpDesktop = @\"winsta0\\default\"", launch, StringComparison.Ordinal);
        }

        [Fact]
        public void TheAction_RunsPadForgeOnlyAtTheSizeThatMadeIt()
        {
            string action = LogonStart.Action(@"C:\PadForge\PadForge.exe", Sid, Natives, 123456);
            Assert.Equal("set \"DOTNET_BUNDLE_EXTRACT_BASE_DIR=C:\\ProgramData\\PadForge\\Natives\" & for %I in (\"C:\\PadForge\\PadForge.exe\") do if \"%~zI\"==\"123456\" ( \"C:\\PadForge\\PadForge.exe\" --logon-start " + Sid + " )", action);
            Assert.Null(LogonStart.Action(@"C:\Pad%Forge\PadForge.exe", Sid, Natives, 1));
            Assert.Null(LogonStart.Action(@"C:\PadForge\PadForge.exe", "nobody", Natives, 1));
            Assert.Null(LogonStart.Action(@"C:\PadForge\PadForge.exe", Sid, Natives, -1));
        }

        [Fact]
        public void TheTask_RunsAsSystem_AtTheUsersLogon_WithNoTimeLimit()
        {
            string xml = LogonStart.TaskXml(Sid, LogonStart.Action(@"C:\PadForge\PadForge.exe", Sid, Natives, 1), @"C:\WINDOWS\system32");
            var doc = XDocument.Parse(xml);
            string Text(string name) => doc.Descendants().First(e => e.Name.LocalName == name).Value;
            var trigger = doc.Descendants().Single(e => e.Name.LocalName == "LogonTrigger");
            Assert.Equal(Sid, trigger.Elements().Single(e => e.Name.LocalName == "UserId").Value);
            var principal = doc.Descendants().Single(e => e.Name.LocalName == "Principal");
            Assert.Equal(LogonStart.SystemSid, principal.Elements().Single(e => e.Name.LocalName == "UserId").Value);
            Assert.Equal("HighestAvailable", Text("RunLevel"));
            Assert.Equal("PT0S", Text("ExecutionTimeLimit"));
            Assert.Equal("IgnoreNew", Text("MultipleInstancesPolicy"));
            Assert.Equal("false", Text("DisallowStartIfOnBatteries"));
            Assert.Equal("false", Text("StopIfGoingOnBatteries"));
            Assert.Equal("false", Text("StopOnIdleEnd"));
            Assert.Equal("false", Text("RunOnlyIfIdle"));
            Assert.Equal("4", Text("Priority"));
            Assert.Equal(@"C:\WINDOWS\system32\cmd.exe", Text("Command"));
            Assert.StartsWith("/q /d /s /c \"set ", Text("Arguments"), StringComparison.Ordinal);
            Assert.DoesNotContain(trigger.Elements(), e => e.Name.LocalName == "Delay");
            Assert.DoesNotContain(XDocument.Parse(LogonStart.TaskXml(Sid, "x", @"C:\WINDOWS\system32", trigger: false)).Descendants(), e => e.Name.LocalName == "LogonTrigger");
        }

        /// <summary>The run is handled in Main after the protected start and
        /// before every other kind of run.</summary>
        [Fact]
        public void Main_HandlesTheRun_RightAfterTheProtectedStart()
        {
            string program = AuditDelta20261002Tests.RepoText("PadForge.App", "Program.cs");
            string main = FenceSource.Body(program, "public static void Main(string[] args)");
            int start = main.IndexOf("ProtectedStart.Run();", StringComparison.Ordinal);
            int logon = main.IndexOf("if (LogonStart.IsRun(args))", StringComparison.Ordinal);
            int giveBack = main.IndexOf("if (GiveBackRun.IsRun(args))", StringComparison.Ordinal);
            Assert.True(start >= 0 && logon > start && giveBack > logon);
        }

        // ── The task the setting makes, and the old form ──

        private const string OldForm =
            "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">"
            + "<Triggers><LogonTrigger><Enabled>true</Enabled></LogonTrigger></Triggers>"
            + "<Principals><Principal id=\"Author\"><UserId>" + Sid + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>"
            + "<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>true</DisallowStartIfOnBatteries>"
            + "<StopIfGoingOnBatteries>true</StopIfGoingOnBatteries><IdleSettings><StopOnIdleEnd>true</StopOnIdleEnd></IdleSettings></Settings>"
            + "<Actions Context=\"Author\"><Exec><Command>\"C:\\PadForge\\PadForge.exe\"</Command></Exec></Actions></Task>";

        [Fact]
        public void TheOldForm_IsReadForItsExeAndUser_AndIsNotCurrent()
        {
            var task = StartupHelper.ReadTask(OldForm);
            Assert.Equal(@"C:\PadForge\PadForge.exe", task.Exe);
            Assert.Equal(Sid, task.Sid);
            Assert.False(StartupHelper.IsCurrent(OldForm, task.Exe, task.Sid, 1000, Natives));
        }

        [Fact]
        public void TheCurrentForm_IsReadBack_AndIsCurrentUntilItsExeChanges()
        {
            string xml = StartupHelper.TaskDefinition(@"C:\PadForge\PadForge.exe", Sid, 1000, Natives);
            var task = StartupHelper.ReadTask(xml);
            Assert.Equal(@"C:\PadForge\PadForge.exe", task.Exe);
            Assert.Equal(Sid, task.Sid);
            Assert.True(StartupHelper.IsCurrent(xml, task.Exe, task.Sid, 1000, Natives));
            // An update changed the exe's size, the move its path, or the
            // unpack folder moved.
            Assert.False(StartupHelper.IsCurrent(xml, task.Exe, task.Sid, 1001, Natives));
            Assert.False(StartupHelper.IsCurrent(xml, @"D:\PadForge\PadForge.exe", task.Sid, 1000, Natives));
            Assert.False(StartupHelper.IsCurrent(xml, task.Exe, task.Sid, 1000, @"D:\ProgramData\PadForge\Natives"));
            Assert.False(StartupHelper.IsCurrent(xml, task.Exe, "S-1-5-21-1-2-3-1002", 1000, Natives));
            Assert.False(StartupHelper.IsCurrent(xml.Replace("PT0S", "PT72H"), task.Exe, task.Sid, 1000, Natives));
            Assert.Equal(Sid, StartupHelper.SidOf(" " + Sid + " "));
            Assert.Null(StartupHelper.SidOf(null));
        }

        /// <summary>Every way the task is made names a user: the switch, the
        /// move, the audit and the migration.</summary>
        [Fact]
        public void EveryTaskMade_NamesItsUser_AndTheAuditRemakesAnOwnFoldersTask()
        {
            string source = AuditDelta20261002Tests.RepoText("PadForge.App", "Common", "StartupHelper.cs");
            Assert.DoesNotContain("CreateScheduledTask(exe)", source, StringComparison.Ordinal);
            Assert.DoesNotContain("/SC ONLOGON", source, StringComparison.Ordinal);
            Assert.DoesNotContain("/RL HIGHEST", source, StringComparison.Ordinal);
            string audit = FenceSource.Body(source, "internal static bool AuditTask()");
            Assert.Contains("if (!IsCurrent(definition, task.Exe, sid, FileLength(task.Exe), ProtectedFolder.Natives))", audit, StringComparison.Ordinal);
            Assert.Contains("CreateScheduledTask(task.Exe, sid);", audit, StringComparison.Ordinal);
            string create = FenceSource.Body(source, "private static bool CreateScheduledTask(string exePath, string sid)");
            Assert.Contains("ProtectedWork.Folder(\"Logon\")", create, StringComparison.Ordinal);
            Assert.Contains("/RU SYSTEM /F", create, StringComparison.Ordinal);
        }

        [Fact]
        public void TheDiagnosticsLog_WithRights_GoesOnlyBesideTheExe()
        {
            Assert.Equal(@"C:\PadForge\diag.log", PadForge.Engine.SdlDiagLog.MirrorPathWithRights(@"C:\PadForge\diag.log", @"C:\PadForge\PadForge.exe"));
            Assert.Equal(@"C:\PadForge\diag.log", PadForge.Engine.SdlDiagLog.MirrorPathWithRights(@"C:\PadForge\sub\..\diag.log", @"C:\PadForge\PadForge.exe"));
            Assert.Null(PadForge.Engine.SdlDiagLog.MirrorPathWithRights(@"C:\Windows\System32\evil.dll", @"C:\PadForge\PadForge.exe"));
            Assert.Null(PadForge.Engine.SdlDiagLog.MirrorPathWithRights(@"C:\PadForge\sub\diag.log", @"C:\PadForge\PadForge.exe"));
            Assert.Null(PadForge.Engine.SdlDiagLog.MirrorPathWithRights(@"C:\PadForge\", @"C:\PadForge\PadForge.exe"));
            Assert.Null(PadForge.Engine.SdlDiagLog.MirrorPathWithRights(null, @"C:\PadForge\PadForge.exe"));
        }

        /// <summary>A start with rights removes the variables that name a
        /// library SDL loads, before it decides anything, so a restarted copy
        /// never has them either.</summary>
        [Fact]
        public void AStartWithRights_DropsTheVariablesThatNameSdlsLibraries()
        {
            Assert.Contains("SDL3_DYNAMIC_API", ProtectedStart.LibraryVariables);
            string source = AuditDelta20261002Tests.RepoText("PadForge.App", "Services", "ProtectedStart.cs");
            string run = FenceSource.Body(source, "internal static void Run()");
            int privileged = run.IndexOf("if (!System32Calls.IsPrivileged()) return;", StringComparison.Ordinal);
            int cleared = run.IndexOf("foreach (string name in LibraryVariables) System32Calls.SetVariable(name, null);", StringComparison.Ordinal);
            int variables = source.IndexOf("string[] variables = System32Calls.Variables();", StringComparison.Ordinal);
            Assert.True(privileged > 0 && cleared > privileged);
            Assert.True(variables > 0);
        }
    }

    /// <summary>A start with rights that cannot go on safely says why in a
    /// message box (#597), through a copy of PadForge without rights.</summary>
    public class StartFailureTests : IDisposable
    {
        public void Dispose() => StartFailure.ShowSeam = null;

        [Fact]
        public void TheBox_NamesTheFolder_InEnglishWhenNoTranslationHasIt()
        {
            var (title, text) = StartFailure.Words(StartFailure.Kind.NoFolder, System.Globalization.CultureInfo.GetCultureInfo("en-US"), @"C:\ProgramData\PadForge");
            Assert.Equal("PadForge", title);
            Assert.Equal(StartFailure.FolderEnglish.Replace("{0}", @"C:\ProgramData\PadForge"), text);
            Assert.Equal(StartFailure.FolderEnglish, PadForge.Resources.Strings.Strings.ResourceManager.GetString(StartFailure.FolderKey, System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(StartFailure.RestartEnglish, PadForge.Resources.Strings.Strings.ResourceManager.GetString(StartFailure.RestartKey, System.Globalization.CultureInfo.InvariantCulture));
            var restart = StartFailure.Words(StartFailure.Kind.NoRestart, null, @"C:\ProgramData\PadForge");
            Assert.Equal(StartFailure.RestartEnglish.Replace("{0}", @"C:\ProgramData\PadForge"), restart.Text);
        }

        /// <summary>The copy looks its text up as the update helper's windows
        /// do: the Windows display language through the language table.</summary>
        [Fact]
        public void TheCopy_FollowsTheWindowsDisplayLanguage()
        {
            var culture = LanguageCatalog.InterfaceCulture(System.Globalization.CultureInfo.GetCultureInfo("de-AT"));
            string want = PadForge.Resources.Strings.Strings.ResourceManager.GetString(StartFailure.FolderKey, culture);
            if (!StartFailure.HoldsOnePlace(want)) want = StartFailure.FolderEnglish;
            Assert.Equal(want.Replace("{0}", "F"), StartFailure.Words(StartFailure.Kind.NoFolder, culture, "F").Text);

            var ui = System.Globalization.CultureInfo.CurrentUICulture;
            try
            {
                System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("de-AT");
                string title = null, shown = null;
                StartFailure.ShowSeam = (t, x) => { title = t; shown = x; };
                Assert.Equal(0, StartFailure.RunInCopy(new[] { StartFailure.Switch, "folder" }));
                Assert.Equal(PadForge.Resources.Strings.Strings.ResourceManager.GetString("Common_PadForge", culture), title);
                Assert.Equal(want.Replace("{0}", ProtectedFolder.Root), shown);
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentUICulture = ui;
            }
            Assert.Equal(2, StartFailure.RunInCopy(new[] { StartFailure.Switch, "calc" }));
            Assert.False(StartFailure.IsRun(new[] { StartFailure.Switch }));
            Assert.True(StartFailure.IsRun(new[] { StartFailure.Switch, "restart" }));
        }

        [Fact]
        public void ATranslation_ThatLostTheFoldersPlace_ReadsEnglish()
        {
            Assert.True(StartFailure.HoldsOnePlace("a {0} b"));
            Assert.False(StartFailure.HoldsOnePlace("a b"));
            Assert.False(StartFailure.HoldsOnePlace("a {0} {0}"));
            Assert.False(StartFailure.HoldsOnePlace("a {0} {1}"));
            Assert.False(StartFailure.HoldsOnePlace("a {0} }"));
            Assert.False(StartFailure.HoldsOnePlace(null));
        }

        /// <summary>The copy unpacks where a start without rights does, is
        /// handed no handover, and gets the allow list's variables
        /// alone.</summary>
        [Fact]
        public void TheCopysVariables_LeaveOutTheProtectedStartsOwn()
        {
            string block = StartFailure.CopyBlock(new[] { "Path=C:\\WINDOWS\\system32" },
                new[] { "=C:=C:\\", "A=1", "dotnet_bundle_extract_base_dir=C:\\x", "PADFORGE_HANDOVER_EVENT=1C", "Path=C:\\mine", "TEMP=C:\\t", "Z=2" });
            Assert.Equal("Path=C:\\WINDOWS\\system32\0TEMP=C:\\t\0\0", block);
        }

        /// <summary>Both ways out show the box before the exit. The start with
        /// rights reads no string and makes no DllImport: it hands the text to
        /// a copy at medium integrity, and only the copy, which refuses to
        /// run with rights, reads the strings.</summary>
        [Fact]
        public void TheStartWithRights_ReadsNoString_AndTheCopyRunsWithoutRights()
        {
            string source = AuditDelta20261002Tests.RepoText("PadForge.App", "Services", "ProtectedStart.cs");
            string run = FenceSource.Body(source, "internal static void Run()");
            Assert.True(run.IndexOf("StartFailure.Show(StartFailure.Kind.NoFolder);", StringComparison.Ordinal)
                        < run.IndexOf("System32Calls.ExitNow(NoSoundFolder);", StringComparison.Ordinal));
            string restart = FenceSource.Body(source, "private static int Restart(string natives)");
            Assert.Contains("if (!started) StartFailure.Show(StartFailure.Kind.NoRestart);", restart, StringComparison.Ordinal);

            string failure = AuditDelta20261002Tests.RepoText("PadForge.App", "Services", "StartFailure.cs");
            string code = string.Join("\n", failure.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
            Assert.DoesNotContain("DllImport(", code, StringComparison.Ordinal);
            Assert.DoesNotContain("Environment.", code, StringComparison.Ordinal);
            Assert.DoesNotContain("throw ", code, StringComparison.Ordinal);
            string show = FenceSource.Body(failure, "internal static void Show(Kind kind)");
            Assert.DoesNotContain("Strings", show, StringComparison.Ordinal);
            Assert.DoesNotContain("Words(", show, StringComparison.Ordinal);
            Assert.DoesNotContain("Culture", show, StringComparison.Ordinal);
            Assert.Contains("System32Calls.StartWithoutRights(", show, StringComparison.Ordinal);
            // No box where no one can answer it.
            Assert.StartsWith("if (System32Calls.IsSystem() || System32Calls.SessionId() is 0 or uint.MaxValue) return;", FenceSource.FirstStatement(failure, "internal static void Show(Kind kind)"), StringComparison.Ordinal);
            string copy = FenceSource.Body(failure, "internal static int RunInCopy(string[] args)");
            Assert.Contains("if (System32Calls.IsSystem() || System32Calls.IntegrityRid() >= System32Calls.HighIntegrityRid) return 3;", copy, StringComparison.Ordinal);

            string program = AuditDelta20261002Tests.RepoText("PadForge.App", "Program.cs");
            string main = FenceSource.Body(program, "public static void Main(string[] args)");
            Assert.True(main.IndexOf("ProtectedStart.Run();", StringComparison.Ordinal) < main.IndexOf("if (StartFailure.IsRun(args))", StringComparison.Ordinal));
            Assert.True(main.IndexOf("if (StartFailure.IsRun(args))", StringComparison.Ordinal) < main.IndexOf("if (LogonStart.IsRun(args))", StringComparison.Ordinal));

            // The copy's token: Administrators for denying only, no
            // privileges, medium integrity.
            string calls = AuditDelta20261002Tests.RepoText("PadForge.App", "Common", "System32Calls.cs");
            string start = FenceSource.Body(calls, "internal static IntPtr StartWithoutRights(string exe, string commandLine, string environmentBlock, string folder)");
            Assert.Contains("fixed (char* a = \"S-1-5-32-544\")", start, StringComparison.Ordinal);
            Assert.Contains("fixed (char* m = \"S-1-16-8192\")", start, StringComparison.Ordinal);
            Assert.Contains("own, DisableMaxPrivilege, 2, disable, 0, null, 0, null, &restricted", start, StringComparison.Ordinal);
            Assert.Contains("NativeLibrary.TryLoad(System32 + \"user32.dll\", out IntPtr user32)", FenceSource.Body(calls, "internal static bool ErrorBox(string text, string title)"), StringComparison.Ordinal);
            Assert.NotEqual(uint.MaxValue, System32Calls.SessionId());
            Assert.False(System32Calls.IsSystem());
            Assert.Equal(System32Calls.MediumIntegrityRid, System32Calls.IntegrityRid());
        }
    }
}
