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
        public void TheScriptsCard_ShowsOnceTheHostHasTheLibraries_AndSaveWritesTheEmbeddedFile()
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
                var ground = new System.Windows.Controls.Border { Child = page };
                page.Show("Automation");
                Lay(ground, dispatcher);
                var card = (FrameworkElement)page.FindName("AutomationScriptsCard");
                Assert.NotNull(card);
                Assert.Equal(Visibility.Collapsed, card.Visibility);

                // What the window hands the host.
                host.Libraries = AutomationLibraries.Instance;
                Lay(ground, dispatcher);
                Assert.Equal(Visibility.Visible, card.Visibility);

                var libraries = vm.AutomationLibraries;
                Assert.Same(AutomationLibraries.Instance, libraries);
                Assert.Equal(new[] { AutomationLibraryKind.AutoHotkey, AutomationLibraryKind.Python, AutomationLibraryKind.PowerShell },
                    System.Linq.Enumerable.Select(libraries.All, l => l.Kind));

                string dir = Path.Combine(Path.GetTempPath(), "PadForge.Tests.LibraryCard." + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                try
                {
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
}
