using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using PadForge.Services.Install;
using PadForge.Services.Plugins;
using System.Windows;
using PadForge.Services.Automation;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Automation page's Scripts card joined to the libraries PadForge
    /// carries (#597): the card shows once the host has the libraries, and
    /// Save a Library through the host writes the embedded file as it is.
    /// </summary>
    [Collection("WpfApplicationSingleton")]
    public class AutomationLibraryCardTests
    {
        private static void Lay(FrameworkElement ground, System.Windows.Threading.Dispatcher dispatcher)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                PageRenderDump.Flush(ground);
                ground.Measure(new Size(1176, 2400));
                ground.Arrange(new Rect(0, 0, 1176, 2400));
                ground.UpdateLayout();
                PageRenderDump.Settle(dispatcher);
            }
        }

        /// <summary>The run as the user, in this process: the test host
        /// starts no install of its own (UserInstall.RunAsUserAsync's
        /// fence), so the page's Save runs the words it would hand
        /// PadForge.exe here.</summary>
        private sealed class InProcessLauncher : IPluginLauncher
        {
            public PluginLaunch Start(PluginCommand command, IReadOnlyDictionary<string, string> variables)
            {
                var words = Regex.Matches(command.CommandLine, "\"([^\"]*)\"|(\\S+)")
                    .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Skip(1).ToList();
                int i = words.IndexOf("--install");
                int code = UserInstall.Parse(words, ref i, out var request) == null
                    ? UserInstall.Run(request, _ => { }, _ => { })
                    : UserInstall.WrongCommandLine;
                return new PluginLaunch { Process = new DoneProcess(code) };
            }
        }

        private sealed class DoneProcess : IPluginProcess
        {
            private readonly int _code;
            public DoneProcess(int code) => _code = code;
            public int Id => 4244;
            public bool Owns(int processId) => processId == Id;
            public event Action<int> Exited
            {
                add => Task.Run(() => value(_code));
                remove { }
            }
            public void Stop() { }
            public void Dispose() { }
        }

        [Fact]
        public void TheScriptsCard_ShowsWithAutomationOff_ChecksEachLibraryAsTheUser_AndSaveWritesTheEmbeddedFile()
        {
            UserInstall.Launcher = new InProcessLauncher();
            try { ShowAndSave(); }
            finally { UserInstall.Launcher = null; }
        }

        private static void ShowAndSave()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                using var host = TestConsent.AllowAll(new AutomationHost(new RecordingAutomationTarget(), new InlineAutomationUi(),
                    "PadForge.Tests.Libraries." + Guid.NewGuid().ToString("N"), _ => AutomationProgram.Unknown));
                var vm = new DashboardViewModel();
                vm.AttachAutomation(host, dispatcher);
                var page = new ExtrasPage { DataContext = vm };
                var card = (FrameworkElement)page.FindName("AutomationScriptsCard");
                Assert.NotNull(card);
                var model = page.ScriptsCardModel;
                // Each check is answered here, as the run as the user would
                // answer it: AutoHotkey current, Python older for one of its
                // two Pythons, PowerShell not installed.
                var asked = new List<InstallRequest>();
                model.RunSeam = request =>
                {
                    lock (asked) asked.Add(request);
                    if (!request.Check) return Task.FromResult(new InstallRun(UserInstall.Done, null, "Installed."));
                    return Task.FromResult(request.What switch
                    {
                        InstallWhat.AutoHotkey => new InstallRun(UserInstall.Done, null, "current\r\nplace current C:\\Users\\u\\Documents\\AutoHotkey\\Lib\\PadForge.ahk\r\n"),
                        InstallWhat.Python => new InstallRun(UserInstall.IsOlder, null,
                            "older\r\nplace older C:\\Users\\u\\AppData\\Roaming\\Python\\Python314\\site-packages\\padforge.py\r\n"
                            + "place missing C:\\Users\\u\\AppData\\Roaming\\Python\\Python313\\site-packages\\padforge.py\r\n"),
                        _ => new InstallRun(UserInstall.IsMissing, null, "not installed\r\nplace missing C:\\Users\\u\\Documents\\PowerShell\\Modules\\PadForge\\PadForge.psm1\r\n"),
                    });
                };
                var ground = new System.Windows.Controls.Border { Child = page };
                page.Show("Automation");
                Lay(ground, dispatcher);
                // Automation is off: the card shows all the same.
                Assert.Equal(Visibility.Visible, card.Visibility);
                // A drawing off screen has no window, so IsVisible stays
                // false here: the check the card runs each time it shows is
                // run by hand, and its wiring read in the page's code.
                Assert.Contains("if (e.NewValue is true) _ = ScriptsCardModel.RefreshAsync();",
                    ExtrasPageTests.RepoText("PadForge.App", "Views", "ExtrasPage.xaml.cs"));
                model.RefreshAsync().Wait();
                PageRenderDump.Settle(dispatcher);
                Assert.Equal(3, asked.Count);
                Assert.All(asked, r => Assert.True(r.Check && r.Target == null));
                var s = PadForge.Resources.Strings.Strings.Instance;
                var rows = model.Rows;
                Assert.Equal(new[] { AutomationLibraryKind.AutoHotkey, AutomationLibraryKind.Python, AutomationLibraryKind.PowerShell }, rows.Select(r => r.Kind));
                Assert.Equal(string.Format(s.Extras_Automation_Scripts_Current_Format, "#Include <PadForge>"), rows[0].Line);
                Assert.Equal(ScriptLibraryButton.OpenFolder, rows[0].Button);
                Assert.Equal(string.Format(s.Extras_Automation_Scripts_OlderFor_Format, "Python 3.14"), rows[1].Line);
                Assert.Equal(ScriptLibraryButton.Update, rows[1].Button);
                Assert.Equal(s.Extras_Automation_Scripts_NotInstalled, rows[2].Line);
                Assert.Equal(ScriptLibraryButton.Install, rows[2].Button);
                Assert.True(model.AnyInstalled);

                // Install runs the install and checks again.
                asked.Clear();
                rows[2].PressCommand.Execute(null);
                PageRenderDump.Settle(dispatcher);
                Assert.Equal(2, asked.Count);
                Assert.False(asked[0].Check);
                Assert.Equal(InstallWhat.PowerShell, asked[0].What);
                Assert.True(asked[1].Check);

                // Open Folder opens the installed copy's folder, as the user.
                string opened = null;
                model.OpenSeam = folder => { opened = folder; return true; };
                rows[0].PressCommand.Execute(null);
                PageRenderDump.Settle(dispatcher);
                Assert.Null(opened);    // C:\Users\u does not exist on this PC: nothing opens.

                string dir = Path.Combine(Path.GetTempPath(), "PadForge.Tests.LibraryCard." + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                try
                {
                    rows[0].Places = new[] { ("current", Path.Combine(dir, "PadForge.ahk")) };
                    rows[0].PressCommand.Execute(null);
                    PageRenderDump.Settle(dispatcher);
                    Assert.Equal(dir, opened);

                    // Save a Library… writes the embedded file as it is.
                    IAutomationLibraries libraries = AutomationLibraries.Instance;
                    foreach (var library in libraries.All)
                    {
                        string path = Path.Combine(dir, library.FileName);
                        // A file already there is replaced.
                        File.WriteAllText(path, "old");
                        libraries.Save(library.Kind, path);
                        using var embedded = typeof(AutomationLibraries).Assembly.GetManifestResourceStream(library.ResourceName);
                        Assert.NotNull(embedded);
                        using var copy = new MemoryStream();
                        embedded.CopyTo(copy);
                        Assert.True(copy.Length > 0, library.FileName + " is empty");
                        Assert.Equal(copy.ToArray(), File.ReadAllBytes(path));
                    }
                }
                finally
                {
                    Directory.Delete(dir, recursive: true);
                }
            }, TimeSpan.FromMinutes(2), "The Scripts card test timed out.");
        }

    }

    /// <summary>A library's check names each place and its state, which
    /// the Scripts card reads (#600 item 26).</summary>
    [Collection("UserInstallStatics")]
    public class ScriptLibraryCheckTests
    {
        [Fact]
        public void ALibrarysCheck_NamesEachPlace_AndItsState()
        {
            string root = Path.Combine(Path.GetTempPath(), "PadForge.Tests.LibraryCheck." + Guid.NewGuid().ToString("N"));
            UserFolders.Seam = kind => Path.Combine(root, kind.ToString());
            try
            {
                var said = new List<string>();
                var request = new InstallRequest(InstallWhat.AutoHotkey, null, Check: true);
                Assert.Equal(UserInstall.IsMissing, UserInstall.Run(request, said.Add, said.Add));
                string file = Path.Combine(root, "Documents", "AutoHotkey", "Lib", "PadForge.ahk");
                Assert.Equal(new[] { "not installed", "place missing " + file }, said);
                Assert.Equal(UserInstall.Done, UserInstall.Run(request with { Check = false }, _ => { }, _ => { }));
                said.Clear();
                Assert.Equal(UserInstall.Done, UserInstall.Run(request, said.Add, said.Add));
                Assert.Equal(new[] { "current", "place current " + file }, said);
                Assert.Equal(new[] { ("current", file) }, ScriptLibraryRows.Places(string.Join("\r\n", said)));
                Assert.Equal("Python 3.14, Python 3.13", ScriptLibraryRows.Pythons(new[]
                {
                    ("current", @"C:\Users\u\AppData\Roaming\Python\Python314\site-packages\padforge.py"),
                    ("older", @"C:\Users\u\AppData\Roaming\Python\Python313-32\site-packages\padforge.py"),
                    ("missing", @"C:\Users\u\AppData\Roaming\Python\Python312\site-packages\padforge.py"),
                }));
            }
            finally
            {
                UserFolders.Seam = null;
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }
    }
}
