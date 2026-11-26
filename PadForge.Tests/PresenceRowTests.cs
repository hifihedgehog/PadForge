using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PadForge.Services.Automation;
using PadForge.Services.Install;
using PadForge.ViewModels;
using Xunit;
using Strings = PadForge.Resources.Strings.Strings;

namespace PadForge.Tests
{
    /// <summary>
    /// The Python check as the Scripts card reads it (owner-reported
    /// 2026-11-26: "I have Python installed on this machine yet PadForge isn't
    /// recognizing its presence"). The Pythons come from PythonsSeam and each
    /// answers through ToolSeam, as uv's 3.11, 3.12 and 3.13 and the install
    /// manager's 3.14 answer on the bench: its version (sys.winver) and its
    /// user site, from one run. The row reads the check's own lines.
    /// </summary>
    [Collection("UserInstallStatics")]
    public sealed class PresenceRowTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "PadForge.Tests.PresenceRows." + Guid.NewGuid().ToString("N"));
        private readonly byte[] _library = AutomationLibraries.Read(AutomationLibraryKind.Python);

        public PresenceRowTests()
        {
            Directory.CreateDirectory(_root);
            UserFolders.Seam = f => Path.Combine(_root, f.ToString());
        }

        public void Dispose()
        {
            UserFolders.Seam = null;
            UserInstall.ToolSeam = null;
            UserInstall.FindSeam = null;
            UserInstall.PythonsSeam = null;
            UserInstall.UserVariableSeam = null;
            UserInstall.Launcher = null;
            UserInstall.ReportFolderSeam = null;
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        }

        private string Plant(params string[] parts)
        {
            string file = Path.Combine(new[] { _root }.Concat(parts).ToArray());
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, "x");
            return file;
        }

        private string Python(string version) => Path.Combine(_root, "uv", "cpython-" + version, "python.exe");

        private string Site(string version)
            => Path.Combine(_root, "Roaming", "Python", "Python" + version.Replace(".", string.Empty), "site-packages");

        /// <summary>The bench's four Pythons, each answering its version and
        /// user site as site.getusersitepackages names it.</summary>
        private void FourPythons(params string[] versions)
        {
            var pythons = versions.ToDictionary(Python, v => v, StringComparer.OrdinalIgnoreCase);
            UserInstall.PythonsSeam = () => pythons.Keys.ToList();
            UserInstall.ToolSeam = (exe, words) => pythons.TryGetValue(exe, out string v) ? (0, v + "\n" + Site(v)) : (-1, null);
        }

        private void Install(string version)
        {
            Directory.CreateDirectory(Site(version));
            File.WriteAllBytes(Path.Combine(Site(version), "padforge.py"), _library);
        }

        private static (int Code, string Said) Check(InstallWhat what = InstallWhat.Python)
        {
            var said = new List<string>();
            int code = UserInstall.Run(new InstallRequest(what, null, Check: true), said.Add, said.Add);
            return (code, string.Join("\r\n", said));
        }

        private static ScriptLibraryRow Row(AutomationLibraryKind kind, int code, string said)
        {
            var row = new ScriptLibraryRow(kind, _ => Task.CompletedTask);
            ScriptLibraryRows.Show(row, new InstallRun(code, null, said));
            return row;
        }

        private static string F(string format, params object[] values) => string.Format(CultureInfo.CurrentCulture, format, values);

        /// <summary>Each Python starts once per check, and the state and the
        /// place lines come from that one run: a Python that answers the
        /// first time and not the second cannot make them disagree.</summary>
        [Fact]
        public void APythonCheck_StartsEachPythonOnce_AndItsStateAgreesWithEveryPlace()
        {
            FourPythons("3.13", "3.14");
            Install("3.14");
            var calls = new List<string>();
            var answer = UserInstall.ToolSeam;
            UserInstall.ToolSeam = (exe, words) =>
            {
                calls.Add(exe);
                // Each Python answers once only.
                return calls.Count(c => string.Equals(c, exe, StringComparison.OrdinalIgnoreCase)) == 1 ? answer(exe, words) : (-1, null);
            };
            var (code, said) = Check();
            Assert.Equal(new[] { Python("3.13"), Python("3.14") }, calls);
            Assert.Equal(2, ScriptLibraryRows.Places(said).Count);
            Assert.Equal(UserInstall.IsPartial, code);
        }

        /// <summary>A library current for one Python and missing for another
        /// is no older copy: the row names the Python it is current for and
        /// the one it is missing for.</summary>
        [Fact]
        public void ALibraryCurrentForOnePython_AndMissingForAnother_IsNoOlderCopy()
        {
            FourPythons("3.11", "3.14");
            Install("3.14");
            var (code, said) = Check();
            Assert.Equal(UserInstall.IsPartial, code);
            var row = Row(AutomationLibraryKind.Python, code, said);
            var s = Strings.Instance;
            Assert.Equal(F(s.Extras_Automation_Scripts_State_CurrentMissing_Format, "Python 3.14", "Python 3.11"), row.Line);
            Assert.Equal(ScriptLibraryButton.Install, row.Button);
        }

        /// <summary>No Python found and Pythons that did not answer are two
        /// answers of their own, neither a failed check.</summary>
        [Fact]
        public void NoPython_AndPythonsThatDidNotAnswer_AreAnswersOfTheirOwn()
        {
            UserInstall.PythonsSeam = () => Array.Empty<string>();
            Assert.Equal(UserInstall.NoPython, Check().Code);
            UserInstall.PythonsSeam = () => new[] { Python("3.12") };
            UserInstall.ToolSeam = (exe, words) => (-1, null);
            Assert.Equal(UserInstall.NoPythonAnswered, Check().Code);
        }

        /// <summary>The bench's state: four Pythons answered, none has the
        /// library. The row under "Python" says the library is missing and
        /// names the four, and the other rows name their language.</summary>
        [Fact]
        public void FourPythonsWithoutTheLibrary_AreNamed_AndTheLibraryIsWhatIsMissing()
        {
            FourPythons("3.11", "3.12", "3.13", "3.14");
            var (code, said) = Check();
            Assert.Equal(UserInstall.IsMissing, code);
            var s = Strings.Instance;
            Assert.Equal(F(s.Extras_Automation_Scripts_State_Missing_Format, "Python 3.11, Python 3.12, Python 3.13, Python 3.14"),
                Row(AutomationLibraryKind.Python, code, said).Line);
            var ahk = Check(InstallWhat.AutoHotkey);
            Assert.Equal(F(s.Extras_Automation_Scripts_State_Missing_Format, "AutoHotkey v2"), Row(AutomationLibraryKind.AutoHotkey, ahk.Code, ahk.Said).Line);
            var ps = Check(InstallWhat.PowerShell);
            Assert.Equal(F(s.Extras_Automation_Scripts_State_Missing_Format, "PowerShell 7, Windows PowerShell"), Row(AutomationLibraryKind.PowerShell, ps.Code, ps.Said).Line);
        }

        /// <summary>A Python registered nowhere is found: pyenv-win's shim on
        /// the user's PATH, and the Pythons the py launcher lists, the Store's
        /// among them. One the registry and the launcher both name is asked
        /// once.</summary>
        [Fact]
        public void APythonOnThePath_AndThePyLaunchersOwn_AreFound_EachOnce()
        {
            string registered = Plant("PythonCore", "3.14", "python.exe");
            string store = Plant("WindowsApps", "PythonSoftwareFoundation.Python.3.12", "python3.12.exe");
            string shim = Plant(".pyenv", "pyenv-win", "shims", "python.bat");
            string py = Plant("Launcher", "py.exe");
            UserInstall.PythonsSeam = () => new[] { registered };
            UserInstall.FindSeam = name => name switch { "python" => shim, "py" => py, _ => null };
            UserInstall.ToolSeam = (exe, words) => exe == py && words.Count == 1 && words[0] == "--list-paths"
                ? (0, " -V:3.14 *        " + registered + "\r\n -V:PythonCore/3.12 " + store + "\r\n")
                : (-1, null);
            Assert.Equal(new[] { registered, store, shim }, UserInstall.Pythons());

            // None registered and no launcher: the PATH's python alone.
            UserInstall.PythonsSeam = () => Array.Empty<string>();
            UserInstall.FindSeam = name => name == "python" ? shim : null;
            Assert.Equal(new[] { shim }, UserInstall.Pythons());

            // The Store's Python alone, with no launcher and nothing on the
            // PATH: its alias in the package's own WindowsApps folder, where
            // the py launcher looks for it (launcher2.c, appxSearch).
            string alias = Plant("LocalData", "Microsoft", "WindowsApps", "PythonSoftwareFoundation.Python.3.13_qbz5n2kfra8p0", "python.exe");
            Plant("LocalData", "Microsoft", "WindowsApps", "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe", "python.exe");
            UserInstall.FindSeam = _ => null;
            Assert.Equal(new[] { alias }, UserInstall.Pythons());
        }

        /// <summary>The check's own script ends its output with a line end,
        /// so a Python's stderr, which RunTool gives after its stdout, never
        /// joins the site's line. Run on the bench's four Pythons, it wrote
        /// "3.14\r\nC:\...\Python314\site-packages" and the like.</summary>
        [Fact]
        public void TheSiteScript_EndsItsLine_AndAPythonsStderrStaysOffTheSite()
        {
            Assert.EndsWith("+ '\\n')", UserInstall.SiteScript);
            UserInstall.PythonsSeam = () => new[] { Python("3.12") };
            UserInstall.ToolSeam = (exe, words) => (0, "3.12\r\n" + Site("3.12") + "\r\nCould not find platform dependent libraries <exec_prefix>\r\n");
            Assert.Equal(Site("3.12"), UserInstall.UserSites(out _).Single().Site);
        }

        /// <summary>PowerShell 7's own module setting and Windows
        /// PowerShell's user-scoped PSModulePath decide where the module
        /// goes, as each reads them.</summary>
        [Fact]
        public void PowerShellsOwnModuleSettings_AreWhereTheModuleGoes()
        {
            string documents = Path.Combine(_root, "Documents");
            string mods = Path.Combine(_root, "Mods"), windows = Path.Combine(_root, "WinMods");
            Directory.CreateDirectory(Path.Combine(documents, "PowerShell"));
            File.WriteAllText(Path.Combine(documents, "PowerShell", "powershell.config.json"),
                "{\n  // the user's own\n  \"PSModulePath\": \"" + mods.Replace("\\", "\\\\") + "\",\n}\n");
            UserInstall.UserVariableSeam = name => name == "PSModulePath" ? windows + @";C:\Other" : null;
            var places = UserInstall.LibraryPlaces(InstallWhat.PowerShell, null);
            Assert.Equal(new[] { Path.Combine(mods, "PadForge", "PadForge.psm1"), Path.Combine(windows, "PadForge", "PadForge.psm1") },
                places.Select(p => p.File));
            Assert.Equal(UserInstall.Done, UserInstall.Run(new InstallRequest(InstallWhat.PowerShell, null), _ => { }, _ => { }));
            Assert.True(File.Exists(places[0].File));
            Assert.True(File.Exists(places[1].File));
            Assert.Equal(UserInstall.Done, Check(InstallWhat.PowerShell).Code);
        }

        /// <summary>The run as the user, in this process: the words it would
        /// hand PadForge.exe run here, its report and all.</summary>
        private sealed class InProcessLauncher : PadForge.Services.Plugins.IPluginLauncher
        {
            public PadForge.Services.Plugins.PluginLaunch Start(PadForge.Services.Plugins.PluginCommand command, IReadOnlyDictionary<string, string> variables)
            {
                var words = System.Text.RegularExpressions.Regex.Matches(command.CommandLine, "\"([^\"]*)\"|(\\S+)")
                    .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Skip(1).ToList();
                int i = words.IndexOf("--install");
                int code = UserInstall.Parse(words, ref i, out var request) == null ? UserInstall.Run(request, _ => { }, _ => { }) : UserInstall.WrongCommandLine;
                return new PadForge.Services.Plugins.PluginLaunch { Process = new DoneProcess(code) };
            }
        }

        private sealed class DoneProcess : PadForge.Services.Plugins.IPluginProcess
        {
            private readonly int _code;
            public DoneProcess(int code) => _code = code;
            public int Id => 4245;
            public bool Owns(int processId) => processId == Id;
            public event Action<int> Exited
            {
                add => Task.Run(() => value(_code));
                remove { }
            }
            public void Stop() { }
            public void Dispose() { }
        }

        /// <summary>A failed run's reason comes back from its report as a
        /// reason, the file it names and no English line of its own, so the
        /// page words it.</summary>
        [Fact]
        public async Task AFailedRunsReason_ComesBackFromItsReport()
        {
            UserInstall.Launcher = new InProcessLauncher();
            UserInstall.ReportFolderSeam = () => _root;
            string mine = Plant("Mine", "padforge.py");
            var run = await UserInstall.RunAsUserAsync(new InstallRequest(InstallWhat.Python, mine, Remove: true));
            Assert.Equal(UserInstall.Failed, run.Code);
            Assert.Equal(InstallReason.NotOurs, run.Reason);
            Assert.Equal(mine, run.Detail);
            Assert.DoesNotContain("reason ", run.Said ?? string.Empty);
            Assert.DoesNotContain("reason ", run.Error ?? string.Empty);
            Assert.True(File.Exists(mine));
            Assert.Empty(Directory.EnumerateFiles(_root, "PadForge-install-*"));
        }

        /// <summary>A free-threaded Python and an ARM64 one are named by the
        /// version each gave (sys.winver 3.14t and 3.13-arm64), whose user
        /// sites are Python314t and Python313-arm64.</summary>
        [Fact]
        public void AFreeThreadedAndAnArm64Python_AreNamedByTheVersionTheyGave()
        {
            FourPythons("3.14t", "3.13-arm64");
            Install("3.14t");
            Install("3.13-arm64");
            Assert.EndsWith(@"Python314t\site-packages", Site("3.14t"));
            var (code, said) = Check();
            Assert.Equal(UserInstall.Done, code);
            Assert.Equal(F(Strings.Instance.Extras_Automation_Scripts_State_Current_Format, "Python 3.14t, Python 3.13-arm64", "import padforge"),
                Row(AutomationLibraryKind.Python, code, said).Line);
        }
    }

    /// <summary>The Scripts card's lines in another language:
    /// a check that found no Python reads the translated line, never the run's
    /// English. The new keys have no German until the translation round, so
    /// the line is the German lookup of its key.</summary>
    [Collection("WpfApplicationSingleton")]
    public sealed class PresenceRowCultureTests
    {
        [Fact]
        public void NoPython_ReadsTheTranslatedLine_AndNoEnglishFromTheRun()
        {
            var before = CultureInfo.CurrentUICulture;
            try
            {
                Strings.ChangeCulture(CultureInfo.GetCultureInfo("de"));
                const string english = "PadForge finds no Python on this PC.";
                var row = new ScriptLibraryRow(AutomationLibraryKind.Python, _ => Task.CompletedTask);
                ScriptLibraryRows.Show(row, new InstallRun(UserInstall.NoPython, null, english));
                Assert.Equal(Strings.Instance.Extras_Automation_Reason_NoPython, row.Line);
                Assert.DoesNotContain(english, row.Line);
                Assert.Equal(ScriptLibraryButton.None, row.Button);
            }
            finally
            {
                Strings.ChangeCulture(before);
            }
        }

        /// <summary>A check that failed reads the translated sentence of its
        /// reason, never the run's English.</summary>
        [Fact]
        public void AFailedCheck_ReadsItsReasonInTheUsersLanguage()
        {
            var before = CultureInfo.CurrentUICulture;
            try
            {
                Strings.ChangeCulture(CultureInfo.GetCultureInfo("de"));
                const string file = @"C:\Users\u\Documents\AutoHotkey\Lib\PadForge.ahk";
                const string english = file + " is not a library PadForge wrote, so PadForge leaves it as it is.";
                var row = new ScriptLibraryRow(AutomationLibraryKind.AutoHotkey, _ => Task.CompletedTask);
                ScriptLibraryRows.Show(row, new InstallRun(UserInstall.Failed, english, english) { Reason = InstallReason.NotOurs, Detail = file });
                var s = Strings.Instance;
                Assert.Equal(string.Format(CultureInfo.CurrentCulture, s.Extras_Automation_Scripts_CheckFailed_Format,
                    string.Format(CultureInfo.CurrentCulture, s.Extras_Automation_Reason_NotOurs_Format, file)), row.Line);
                Assert.DoesNotContain("PadForge wrote", row.Line);
                Assert.StartsWith("PadForge konnte", row.Line);
            }
            finally
            {
                Strings.ChangeCulture(before);
            }
        }

        /// <summary>The card's words follow a change of language while the
        /// page and its model stay: a row's line and its button's words are
        /// raised again in the new language.</summary>
        [Fact]
        public void TheScriptsCard_FollowsALanguageChange()
        {
            var before = CultureInfo.CurrentUICulture;
            try
            {
                Strings.ChangeCulture(CultureInfo.GetCultureInfo("en"));
                var model = new ScriptLibraryRows();
                var ahk = model.Rows.Single(r => r.Kind == AutomationLibraryKind.AutoHotkey);
                var python = model.Rows.Single(r => r.Kind == AutomationLibraryKind.Python);
                ScriptLibraryRows.Show(ahk, new InstallRun(UserInstall.Done, null, "current\r\nplace current autohotkey C:\\Users\\u\\Documents\\AutoHotkey\\Lib\\PadForge.ahk"));
                string checking = python.Line;
                var raised = new List<string>();
                ahk.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
                python.PropertyChanged += (_, e) => raised.Add("python " + e.PropertyName);

                Strings.ChangeCulture(CultureInfo.GetCultureInfo("de"));
                Assert.Contains(nameof(ScriptLibraryRow.ButtonWords), raised);
                Assert.Equal(Strings.Instance.Settings_OpenFolder, ahk.ButtonWords);
                Assert.Contains("python " + nameof(ScriptLibraryRow.Line), raised);
                Assert.Equal(Strings.Instance.Extras_Automation_Scripts_Checking, python.Line);
                Assert.NotEqual(checking, python.Line);
                GC.KeepAlive(model);
            }
            finally
            {
                Strings.ChangeCulture(before);
            }
        }
    }
}
