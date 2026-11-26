using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using PadForge.Common.Input;
using PadForge.Services;
using PadForge.Services.Automation;
using PadForge.Services.Catalog;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// What the window gives an assistant's tools: the system read's rows as
    /// the Drivers card reads them, a profile export with no file in the
    /// user's temp folder, a recording's summary read within the recording
    /// reader's limits, and the tray's Mark the Recording only while a
    /// recording is captured.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class WindowAssistantReadTests
    {
        private static AssistantDriverState Row(SettingsViewModel s, string name)
            => Assert.Single(MainWindow.DriverRows(s), r => r.Name == name);

        /// <summary>SDL that did not load reads as not installed, with no
        /// version.</summary>
        [Fact]
        public void SdlThatDidNotLoad_ReadsNotInstalled()
        {
            var s = new SettingsViewModel();
            s.SdlVersion = "3.4.0";
            Assert.True(Row(s, "SDL").Installed);
            Assert.Equal("3.4.0", Row(s, "SDL").Version);
            s.SdlVersionState = SettingsViewModel.SdlVersionKind.NotFound;
            Assert.False(Row(s, "SDL").Installed);
            Assert.Null(Row(s, "SDL").Version);
        }

        /// <summary>Windows MIDI Services reads as there when either of its
        /// APIs runs, the one built into Windows included.</summary>
        [Fact]
        public void WindowsMidiServices_BuiltIntoWindows_ReadsInstalled()
        {
            var s = new SettingsViewModel { IsMidiRuntimeInstalled = false, ActiveMidiApi = MidiApiKind.InBox };
            Assert.True(Row(s, "Windows MIDI Services").Installed);
            Assert.Null(Row(s, "Windows MIDI Services").Version);
            s.ActiveMidiApi = MidiApiKind.Legacy;
            Assert.False(Row(s, "Windows MIDI Services").Installed);
            s.IsMidiRuntimeInstalled = true;
            s.MidiRuntimeVersion = "1.0.16";
            s.ActiveMidiApi = MidiApiKind.AppSdk;
            Assert.True(Row(s, "Windows MIDI Services").Installed);
            Assert.Equal("1.0.16", Row(s, "Windows MIDI Services").Version);
        }

        /// <summary>The list holds BthPS3, as the Drivers card does, with its
        /// version only while it is installed.</summary>
        [Fact]
        public void TheList_HoldsBthPS3()
        {
            var s = new SettingsViewModel { IsBthPS3Installed = false };
            Assert.False(Row(s, "BthPS3").Installed);
            Assert.Null(Row(s, "BthPS3").Version);
            s.IsBthPS3Installed = true;
            Assert.True(Row(s, "BthPS3").Installed);
        }

        /// <summary>An export writes nothing into the user's temp folder, and
        /// its bytes are the profile's file.</summary>
        [Fact]
        public void AnExport_PutsNoFileInTheTempFolder()
        {
            var profile = new ProfileData { Id = "p-export", Name = "Export Test" };
            var seen = new List<string>();
            using var watch = new FileSystemWatcher(Path.GetTempPath(), "PadForge-export-*") { IncludeSubdirectories = false };
            watch.Created += (s, e) => { lock (seen) seen.Add(e.Name); };
            watch.EnableRaisingEvents = true;
            byte[] bytes = MainWindow.ExportFile(profile);
            Thread.Sleep(500);
            lock (seen) Assert.Empty(seen);
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            Assert.NotNull(zip.GetEntry("profile.xml"));
        }

        /// <summary>A recording's file is read within the reader's limits: a
        /// line past the longest is refused.</summary>
        [Fact]
        public void ARecordingWithAnEndlessLine_IsRefused()
        {
            string zip = Path.Combine(Path.GetTempPath(), "PadForge-summary-" + Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                using (var file = new ZipArchive(File.Create(zip), ZipArchiveMode.Create))
                {
                    var entry = file.CreateEntry("recording.log");
                    using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                    w.Write(new string('x', PadForge.Engine.Recording.BoundedLineReader.LongestLine * 4));
                }
                var refusal = Assert.Throws<AssistantRefusal>(() => MainWindow.SummaryOf(zip));
                Assert.Equal("internal", refusal.Code);
            }
            finally
            {
                File.Delete(zip);
            }
        }

        /// <summary>
        /// The test that reads the catalog under the app's resources.
        /// AutomationAssistantCatalogTests.WithPages makes PadForge's App
        /// through PageRenderDumpPages.WithApp and puts the dark theme on. The
        /// class above runs beside the other collections, where that App
        /// restyled windows other tests had open on their own threads and the
        /// dark theme stayed for the tests after it. So this runs in the
        /// WpfApplicationSingleton collection, which runs alone, as WithApp
        /// asks.
        /// </summary>
        [Collection("WpfApplicationSingleton")]
        public class CatalogTests
        {
            /// <summary>Mark the Recording is in the catalog only while a
            /// recording is captured, and a press of it with none is refused. A
            /// press goes to the recorder's own Mark, as the tray's item does.</summary>
            [Fact]
            public void MarkTheRecording_ShowsOnlyWhileARecordingIsCaptured()
            {
                var shows = TrayMenu.MarkShows;
                try
                {
                    AutomationAssistantCatalogTests.WithPages(p =>
                    {
                        TrayMenu.MarkShows = () => false;
                        var menus = p.Catalog.Find(null, "menu_item", 400).Places;
                        Assert.DoesNotContain(menus, m => m.Id.EndsWith("/Tray_MarkRecording", StringComparison.Ordinal));
                        Assert.Contains(menus, m => m.Id.EndsWith("/Tray_Exit", StringComparison.Ordinal));
                        Assert.Equal("not-available", Assert.Throws<AssistantRefusal>(() => p.Catalog.PlanPress("window.menuitem/Tray/Tray_MarkRecording")).Code);
                        TrayMenu.MarkShows = () => true;
                        Assert.Contains(p.Catalog.Find(null, "menu_item", 400).Places, m => m.Id.EndsWith("/Tray_MarkRecording", StringComparison.Ordinal));
                    });
                }
                finally
                {
                    TrayMenu.MarkShows = shows;
                }
                string assistant = File.ReadAllText(Path.Combine(ReachMarkup.AppRoot, "MainWindow.Assistant.cs"));
                int start = assistant.IndexOf("PressTray = key =>", StringComparison.Ordinal);
                int end = assistant.IndexOf("MainWindow = () => this,", start, StringComparison.Ordinal);
                Assert.Contains("else if (key == \"Mark\") Services.ProblemRecorder.Instance.Mark();", assistant.Substring(start, end - start));
            }
        }
    }
}
