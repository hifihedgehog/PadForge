using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using System.Xml.Serialization;
using PadForge.Common.Input;
using PadForge.Engine.Data;
using PadForge.Search;
using PadForge.Services;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;
using static PadForge.Tests.ControllerDatabaseFixture;

namespace PadForge.Tests
{
    /// <summary>
    /// The controller database section through the real settings save and
    /// load (#597), against the baseline this build carries: it is written
    /// into PadForge.xml and nowhere else, it is left out when there is
    /// none, a file from before the feature loads, and a load that reads
    /// the section as another one saves the file again.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class ControllerDatabaseSettingsTests : IDisposable
    {
        private readonly ControllerDatabaseSection _section = SettingsManager.ControllerDatabase;
        private readonly string _from = SettingsManager.ControllerDatabaseFrom;
        private readonly SettingsCollection _settings = SettingsManager.UserSettings;
        private readonly DeviceCollection _devices = SettingsManager.UserDevices;
        private readonly List<ProfileData> _profiles = SettingsManager.Profiles;
        private readonly string _active = SettingsManager.ActiveProfileId;
        private readonly ProfileData _pendingDefault = SettingsManager.PendingDefaultSnapshot;
        private readonly bool[] _created = SettingsManager.SlotCreated;
        private readonly bool[] _enabled = SettingsManager.SlotEnabled;
        private readonly MappingSet[] _sets = SettingsManager.SlotMappingSets;
        private readonly List<string> _files = new();

        public void Dispose()
        {
            SettingsManager.ControllerDatabase = _section;
            SettingsManager.ControllerDatabaseFrom = _from;
            SettingsManager.UserSettings = _settings;
            SettingsManager.UserDevices = _devices;
            SettingsManager.Profiles = _profiles;
            SettingsManager.ActiveProfileId = _active;
            SettingsManager.PendingDefaultSnapshot = _pendingDefault;
            SettingsManager.SlotCreated = _created;
            SettingsManager.SlotEnabled = _enabled;
            SettingsManager.SlotMappingSets = _sets;
            foreach (string file in _files)
                try { File.Delete(file); } catch { }
        }

        private static (MainViewModel Vm, SettingsService Settings) Arrange()
        {
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.Profiles = new List<ProfileData>();
            SettingsManager.ActiveProfileId = null;
            SettingsManager.PendingDefaultSnapshot = null;
            SettingsManager.SlotCreated = new bool[InputManager.MaxPads];
            SettingsManager.SlotEnabled = new bool[InputManager.MaxPads];
            SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
            var vm = new MainViewModel();
            var settings = new SettingsService(vm);
            _ = new InputService(vm) { SettingsService = settings };
            return (vm, settings);
        }

        private string TempFile()
        {
            string path = Path.Combine(Path.GetTempPath(), "PadForgeControllerDatabase_" + Guid.NewGuid().ToString("N") + ".xml");
            _files.Add(path);
            return path;
        }

        private static readonly DateTime Fetched = new(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        /// <summary>A section against the real baseline: a changed form of a
        /// community row the fork carries, and a row for a controller the
        /// pinned copy does not have.</summary>
        private static ControllerDatabaseSection RealSection(out string changedId, out string newId)
        {
            var baseline = ControllerDatabaseBaseline.Embedded;
            changedId = baseline.Entries.First(e => e.Value.Verdict == ControllerDatabaseVerdict.Kept).Key;
            newId = Id(0xFFF1, 0xFFF2);
            Assert.False(baseline.TryFind(newId, out _));
            string changed = changedId + ",A Changed Community Pad,a:b0,b:b1,platform:Windows,";
            string added = Row(0xFFF1, 0xFFF2, "A New Pad");
            var data = new ControllerDatabaseData
            {
                Source = ControllerDatabaseAddress.Community,
                FetchedUtc = "2030-01-02T03:04:05Z",
                ETag = "W/\"real\"",
                Sha256 = new string('b', 64),
                WindowsRows = "873",
                BaselinePin = baseline.PinCommit,
                AppVersion = BuildIdentity.Display,
                Rows = new[]
                {
                    new ControllerDatabaseRowData { Kind = "Changed", Text = changed },
                    new ControllerDatabaseRowData { Kind = "New", InUse = "true", Text = added },
                }.OrderBy(r => r.Text.Substring(0, 32), StringComparer.Ordinal).ToArray(),
            };
            var section = ControllerDatabaseSection.FromData(data, baseline, BuildIdentity.Display, out bool changedOnRead);
            Assert.False(changedOnRead);
            Assert.Equal(2, section.Rows.Count);
            return section;
        }

        [Fact]
        public void TheSection_GoesIntoTheSettingsFile_AndComesBackFromIt()
        {
            var (vm, settings) = Arrange();
            var section = RealSection(out string changedId, out string newId);
            SettingsManager.ControllerDatabase = section;
            string path = TempFile();
            Assert.True(settings.SaveToFile(path));

            var root = XDocument.Load(path).Root;
            var element = Assert.Single(root.Elements(), e => e.Name.LocalName == "ControllerDatabase");
            Assert.Equal(2, element.Elements().Count(e => e.Name.LocalName == "Row"));
            Assert.Equal("2030-01-02T03:04:05Z", (string)element.Attribute("FetchedUtc"));
            // The save wrote the one file, and nothing beside it.
            Assert.Equal(new[] { Path.GetFileName(path) },
                Directory.GetFiles(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path) + "*").Select(Path.GetFileName));

            SettingsManager.ControllerDatabase = null;
            var (vm2, settings2) = Arrange();
            settings2.LoadFromFile(path);
            var loaded = SettingsManager.ControllerDatabase;
            Assert.NotNull(loaded);
            Assert.Equal(Fetched, loaded.FetchedUtc);
            Assert.Equal("W/\"real\"", loaded.ETag);
            Assert.Equal(873, loaded.WindowsRows);
            Assert.Equal(section.Rows.Select(r => (r.Row.Text, r.Kind, r.InUse)), loaded.Rows.Select(r => (r.Row.Text, r.Kind, r.InUse)));
            Assert.True(loaded.TryFindNew(newId, out var added) && added.InUse);
            Assert.Contains(loaded.Rows, r => r.Row.Guid == changedId && r.Kind == ControllerDatabaseRowKind.Changed);
            GC.KeepAlive(vm);
            GC.KeepAlive(vm2);
        }

        [Fact]
        public void WithNoSection_TheSettingsFileHasNoElement_AndLoadsWithNone()
        {
            var (_, settings) = Arrange();
            SettingsManager.ControllerDatabase = null;
            string path = TempFile();
            Assert.True(settings.SaveToFile(path));
            Assert.DoesNotContain("ControllerDatabase", File.ReadAllText(path), StringComparison.Ordinal);

            // A load of a file without the element clears a section in force.
            SettingsManager.ControllerDatabase = RealSection(out _, out _);
            var (_, settings2) = Arrange();
            settings2.LoadFromFile(path);
            Assert.Null(SettingsManager.ControllerDatabase);
        }

        /// <summary>Download From (#600): the address the user set goes into
        /// the file with no download and no row of theirs, comes back on
        /// load, and a file that holds one PadForge does not take loads with
        /// the community's and is saved again. Reset to Defaults puts the
        /// community's back.</summary>
        [Fact]
        public void TheDownloadAddress_IsSavedAndLoaded_AndAnAddressPadForgeDoesNotTakeIsDropped()
        {
            const string mirror = "https://mirror.example.net/sdl/gamecontrollerdb.txt";
            var (_, settings) = Arrange();
            SettingsManager.ControllerDatabase = null;
            SettingsManager.ControllerDatabaseFrom = mirror;
            string path = TempFile();
            Assert.True(settings.SaveToFile(path));
            Assert.Contains("DownloadFrom=\"" + mirror + "\"", File.ReadAllText(path), StringComparison.Ordinal);

            SettingsManager.ControllerDatabaseFrom = null;
            var (vm, settings2) = Arrange();
            settings2.LoadFromFile(path);
            Assert.Equal(mirror, SettingsManager.ControllerDatabaseFrom);
            Assert.Equal(mirror, vm.Settings.ControllerDatabaseAddress);
            Assert.Null(SettingsManager.ControllerDatabase);

            settings2.ResetToDefaults();
            Assert.Equal(ControllerDatabaseAddress.Community, SettingsManager.ControllerDatabaseFrom);

            foreach (string odd in new[] { "http://mirror.example.net/plain.txt", "https://MIRROR.example.net/sdl/gamecontrollerdb.txt" })
            {
                string other = TempFile();
                WriteFile(other, new ControllerDatabaseData { DownloadFrom = odd });
                SettingsManager.ControllerDatabaseFrom = mirror;
                var (_, settings3) = Arrange();
                PathField.SetValue(settings3, other);
                settings3.LoadFromFile(other);
                Assert.Equal(ControllerDatabaseAddress.Community, SettingsManager.ControllerDatabaseFrom);
                Assert.True((bool)typeof(SettingsService).GetField("_controllerDatabaseChangedOnLoad", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(settings3));
            }
            GC.KeepAlive(vm);
        }

        /// <summary>A file from before the feature, as an older PadForge
        /// wrote it, loads with no section and no complaint.</summary>
        [Fact]
        public void AnOldSettingsFile_LoadsWithNoSection()
        {
            string path = TempFile();
            var serializer = new XmlSerializer(typeof(SettingsFileData));
            using (var writer = new StreamWriter(path))
                serializer.Serialize(writer, new SettingsFileData { AppSettings = new AppSettingsData() });
            Assert.DoesNotContain("ControllerDatabase", File.ReadAllText(path), StringComparison.Ordinal);

            SettingsManager.ControllerDatabase = RealSection(out _, out _);
            var (vm, settings) = Arrange();
            settings.LoadFromFile(path);
            Assert.Null(SettingsManager.ControllerDatabase);
            // The load did not set the file aside as one it could not read.
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path), Path.GetFileName(path) + ".unreadable-*"));
            GC.KeepAlive(vm);
        }

        /// <summary>A section the file cannot make sense of is no section.
        /// The rest of the file loads, and no copy of it is set aside as
        /// unreadable.</summary>
        [Fact]
        public void ASectionThatCannotBeRead_IsDropped_AndTheFileStillLoads()
        {
            var (_, settings) = Arrange();
            string path = TempFile();
            SettingsManager.ControllerDatabase = RealSection(out _, out _);
            Assert.True(settings.SaveToFile(path));
            string xml = File.ReadAllText(path).Replace("FetchedUtc=\"2030-01-02T03:04:05Z\"", "FetchedUtc=\"some day\"");
            Assert.Contains("some day", xml, StringComparison.Ordinal);
            File.WriteAllText(path, xml);

            var (vm, settings2) = Arrange();
            vm.Settings.AutoStartEngine = false;
            settings2.LoadFromFile(path);
            Assert.Null(SettingsManager.ControllerDatabase);
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path), Path.GetFileName(path) + ".unreadable-*"));
            // The rest of the file was read: the default it holds is back.
            Assert.True(vm.Settings.AutoStartEngine);
        }

        private static readonly FieldInfo PathField =
            typeof(SettingsService).GetField("_settingsFilePath", BindingFlags.Instance | BindingFlags.NonPublic);

        private void WriteFile(string path, ControllerDatabaseData section)
        {
            // A usable access code, so nothing but the section can owe a save.
            var app = new AppSettingsData { WebControllerAccessCodeProtected = WebControllerAccess.ProtectForStorage("MNPQRSTVWX") };
            var serializer = new XmlSerializer(typeof(SettingsFileData));
            using var writer = new StreamWriter(path);
            serializer.Serialize(writer, new SettingsFileData { AppSettings = app, ControllerDatabase = section });
        }

        /// <summary>The user's own rows (#600) go into the file beside the
        /// downloaded ones and come back through the real load, and a section
        /// another pin stored keeps them while its downloaded rows are judged
        /// again: a new release does not judge a row of the user's.</summary>
        [Fact]
        public void TheUsersRows_SurviveTheLoad_AndAnotherPin()
        {
            var savedMine = ControllerDatabaseMine.Current;
            try
            {
                var section = RealSection(out _, out _);
                var data = section.ToData();
                data.BaselinePin = new string('9', 40);
                data.Mine = new[]
                {
                    new ControllerDatabaseMineData { Added = "2030-01-02T03:04:05Z", Old = string.Empty, Mapped = Guid.Empty.ToString(),
                        Text = "050057564c050000e60c000000006800,Mine,a:b1,platform:Windows," },
                };
                string path = TempFile();
                var (_, settings) = Arrange();
                WriteFile(path, data);
                PathField.SetValue(settings, path);
                settings.Reload();

                var mine = Assert.Single(ControllerDatabaseMine.Current.Rows);
                Assert.Equal("050057564c050000e60c000000006800", mine.Id);
                Assert.Equal(string.Empty, mine.OldRow);
                Assert.Equal(new[] { Guid.Empty.ToString() }, mine.Mapped);

                Assert.True(settings.SaveToFile(path));
                var element = Assert.Single(XDocument.Load(path).Root.Elements(), e => e.Name.LocalName == "ControllerDatabase");
                Assert.Single(element.Elements(), e => e.Name.LocalName == "Mine");
            }
            finally
            {
                ControllerDatabaseMine.Current = savedMine;
            }
        }

        /// <summary>The prune a new version owes, through the real load: a
        /// section another build stored is read against this build's
        /// baseline, and the reload re-arms the save after its own dirty
        /// clear so the file follows. A section this build stored leaves
        /// nothing to save.</summary>
        [Fact]
        public void AReloadOfAnotherBuildsSection_SavesTheFileAgain_AndThisBuildsOwnDoesNot()
        {
            var section = RealSection(out _, out string newId);
            string path = TempFile();

            var (_, settings) = Arrange();
            WriteFile(path, section.ToData());
            PathField.SetValue(settings, path);
            settings.Reload();
            Assert.False(settings.IsDirty);
            Assert.Equal(2, SettingsManager.ControllerDatabase.Rows.Count);

            // The same section as an older build stored it, with a row this
            // build's SDL maps in its own Windows section.
            var baseline = ControllerDatabaseBaseline.Embedded;
            string sdlId = baseline.Entries.First(e => e.Value.Verdict == ControllerDatabaseVerdict.SdlWindows
                                                       && ControllerDatabaseRow.TryReadUsbGuid(e.Key, out _, out _)).Key;
            var older = section.ToData();
            older.AppVersion = "5.9.9";
            older.Rows = older.Rows.Append(new ControllerDatabaseRowData { Kind = "New", Text = sdlId + ",Now SDL's Own,a:b0,platform:Windows," }).ToArray();
            var (_, settings2) = Arrange();
            WriteFile(path, older);
            PathField.SetValue(settings2, path);
            settings2.Reload();
            Assert.True(settings2.IsDirty);
            var loaded = SettingsManager.ControllerDatabase;
            Assert.Equal(2, loaded.Rows.Count);
            Assert.DoesNotContain(loaded.Rows, r => r.Row.Guid == sdlId);
            Assert.True(loaded.TryFindNew(newId, out _));
            Assert.Equal(BuildIdentity.Display, loaded.AppVersion);
        }

        [Fact]
        public void BothLoads_ReArmTheSaveForAChangedSection()
        {
            string service = OffLimitsSource.Read("PadForge.App/Services/SettingsService.cs");
            const string rearm = "if (_controllerDatabaseChangedOnLoad) { _controllerDatabaseChangedOnLoad = false; MarkDirty(); }";
            Assert.Contains(rearm, FenceSource.Body(service, "public void Initialize()"), StringComparison.Ordinal);
            Assert.Contains(rearm, FenceSource.Body(service, "public void Reload()"), StringComparison.Ordinal);
            string save = FenceSource.Body(service, "public bool SaveToFile(string filePath)");
            Assert.Contains("data.ControllerDatabase = ControllerDatabaseSection.Compose(SettingsManager.ControllerDatabase, ControllerDatabaseMine.Current,",
                save, StringComparison.Ordinal);
            // With the address the user set under Download From (#600).
            Assert.Contains("SettingsManager.ControllerDatabaseFrom);", save, StringComparison.Ordinal);
        }

        /// <summary>Reset to Defaults is a fresh install's state, and a
        /// fresh install has downloaded nothing.</summary>
        [Fact]
        public void ResetToDefaults_EmptiesTheSection()
        {
            var (vm, settings) = Arrange();
            SettingsManager.ControllerDatabase = RealSection(out _, out _);
            var raised = new List<string>();
            vm.Settings.PropertyChanged += (s, e) => raised.Add(e.PropertyName);
            settings.ResetToDefaults();
            Assert.Null(SettingsManager.ControllerDatabase);
            Assert.Contains(nameof(SettingsViewModel.ControllerDatabaseStatus), raised);
        }

        /// <summary>The section is the app's, not a profile's: no profile
        /// carries it, and a profile switch leaves it alone.</summary>
        [Fact]
        public void TheSection_IsNoPartOfAProfile()
        {
            Assert.Null(typeof(ProfileData).GetProperty("ControllerDatabase"));
            Assert.Null(typeof(AppSettingsData).GetProperty("ControllerDatabase"));
            Assert.NotNull(typeof(SettingsFileData).GetProperty("ControllerDatabase"));
        }
    }

    /// <summary>The Controller Database row on the Settings page's Updates
    /// card (#597), read from the page's markup: where it sits, its button,
    /// its reset, and what Search finds of it.</summary>
    public class ControllerDatabaseRowTests
    {
        private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

        private static XDocument Page() => XDocument.Parse(ExtrasPageTests.RepoText("PadForge.App", "Views", "SettingsPage.xaml"));

        private static string Key(string name) => "{Binding " + name + ", Source={x:Static strings:Strings.Instance}}";

        private static XElement Card(XDocument page) => page.Descendants().Single(e =>
            e.Attributes().Any(a => a.Name.LocalName == "SettingSearch.Card" && a.Value == Key("Settings_Updates")));

        private static XElement RowOf(XDocument page) => Card(page).Descendants().Single(e => e.Name.LocalName == "SettingRow"
            && e.Elements().Any(c => c.Name.LocalName == "TextBlock" && (string)c.Attribute("Text") == Key("Settings_ControllerDatabase")));

        /// <summary>One row, in the Updates card, in the card's one label
        /// column, inside an item as every row the page's search can hide
        /// is.</summary>
        [Fact]
        public void TheRow_SitsInTheUpdatesCard_WithTheCardsLabelStyle()
        {
            var page = Page();
            var row = RowOf(page);
            Assert.Equal("SettingItem", row.Parent.Name.LocalName);
            var label = row.Elements().First();
            Assert.Equal("{StaticResource DzLabel}", (string)label.Attribute("Style"));
            Assert.Equal(Key("Settings_ControllerDatabaseDesc"), (string)label.Attribute("ToolTip"));
            // The page names the row nowhere else.
            Assert.Single(page.Descendants(), e => (string)e.Attribute("Text") == Key("Settings_ControllerDatabase"));
            // Every other row of the card uses the same label style.
            var labels = Card(page).Descendants().Where(e => e.Name.LocalName == "SettingRow").Select(r => r.Elements().First())
                .Where(e => e.Name.LocalName == "TextBlock").ToList();
            Assert.True(labels.Count >= 4);
            Assert.All(labels, l => Assert.Equal("{StaticResource DzLabel}", (string)l.Attribute("Style")));
        }

        /// <summary>The button is Update Now, says Updating while a download
        /// runs, and its words come from the string table.</summary>
        [Fact]
        public void TheButton_SaysUpdateNow_AndUpdatingWhileADownloadRuns()
        {
            var row = RowOf(Page());
            var button = Assert.Single(row.Elements(), e => e.Name.LocalName == "Button");
            Assert.Equal("{Binding UpdateControllerDatabaseCommand}", (string)button.Attribute("Command"));
            Assert.Null(button.Attribute("Content"));
            var setters = button.Descendants().Where(e => e.Name.LocalName == "Setter" && (string)e.Attribute("Property") == "Content").ToList();
            Assert.Equal(2, setters.Count);
            Assert.Equal(Key("Settings_ControllerDatabaseUpdateNow"), (string)setters[0].Attribute("Value"));
            Assert.Equal("Style", setters[0].Parent.Name.LocalName);
            Assert.Equal(Key("Settings_ControllerDatabaseUpdating"), (string)setters[1].Attribute("Value"));
            var trigger = setters[1].Parent;
            Assert.Equal("DataTrigger", trigger.Name.LocalName);
            Assert.Equal("{Binding IsControllerDatabaseUpdating}", (string)trigger.Attribute("Binding"));
            Assert.Equal("True", (string)trigger.Attribute("Value"));
        }

        /// <summary>The status line stands in the row as a row's report
        /// does, and the line under the row turns to the warning color when
        /// an update did not go through.</summary>
        [Fact]
        public void TheStatusLine_IsTheRowsReport_AndTheLineUnderItCanWarn()
        {
            var row = RowOf(Page());
            var status = Assert.Single(row.Elements(), e => (string)e.Attribute("Text") == "{Binding ControllerDatabaseStatus}");
            Assert.Equal("{StaticResource RowStatus}", (string)status.Attribute("Style"));
            Assert.Equal("True", status.Attributes().Single(a => a.Name.LocalName == "LiveText.Polite").Value);

            var result = Assert.Single(row.Parent.Elements(), e => (string)e.Attribute("Text") == "{Binding ControllerDatabaseResult}");
            Assert.Equal("Wrap", (string)result.Attribute("TextWrapping"));
            // 12 is the smallest text size.
            Assert.True(double.Parse((string)result.Attribute("FontSize"), System.Globalization.CultureInfo.InvariantCulture) >= 12);
            var warn = Assert.Single(result.Descendants(), e => e.Name.LocalName == "DataTrigger");
            Assert.Equal("{Binding ControllerDatabaseResultIsWarning}", (string)warn.Attribute("Binding"));
            Assert.Contains("WaitBrush", (string)warn.Elements().Single().Attribute("Value"), StringComparison.Ordinal);
        }

        /// <summary>The row's reset is the page's generic one, bound to the
        /// row, named by the row's label, and on the list of reset
        /// bindings.</summary>
        [Fact]
        public void TheReset_IsTheRowsOwn_AndOnTheListOfResetBindings()
        {
            var row = RowOf(Page());
            var reset = Assert.Single(row.Elements(), e => e.Name.LocalName == "SettingResetButton");
            Assert.Same(reset, row.Elements().Last());
            Assert.Equal("{Binding ResetSettingCommand}", (string)reset.Attribute("Command"));
            Assert.Equal("ControllerDatabaseStatus", (string)reset.Attribute("CommandParameter"));
            Assert.Equal(Key("Settings_ControllerDatabase"), (string)reset.Attribute("SettingLabel"));
            string id = reset.Attributes().Single(a => a.Name.LocalName == "AutomationProperties.AutomationId").Value;
            Assert.Equal("Reset_SettingsViewModel_ControllerDatabaseStatus_5971", id);

            Assert.True(SettingsViewModel.CanResetSetting(nameof(SettingsViewModel.ControllerDatabaseStatus)));
            string bindings = ExtrasPageTests.RepoText("PadForge.Tests", "ResetControlBindings.json");
            Assert.Contains("\"Id\": \"" + id + "\"", bindings, StringComparison.Ordinal);
            Assert.Contains("\"Property\": \"ControllerDatabaseStatus\"", bindings, StringComparison.Ordinal);
        }

        /// <summary>Search finds the row by its name, in the Updates card,
        /// and its button as an action of the row.</summary>
        [Fact]
        public void Search_FindsTheRowAndItsButton()
        {
            var markup = SearchSources.FileNames.ToDictionary(p => p.Key, p => ExtrasPageTests.RepoText("PadForge.App", "Views", p.Value));
            var index = SearchIndexBuilder.Read(markup, PadPage.TabOfGroupName);

            var setting = Assert.Single(index.Entries, e => e.Kind == SearchKind.Setting && e.Label.Key == "Settings_ControllerDatabase");
            Assert.Equal(SearchPage.Settings, setting.Page);
            Assert.Equal("Settings_Updates", setting.CardTitle?.Key);
            Assert.Equal(SearchLabelSource.Text, setting.LabelSource);
            Assert.False(setting.UnderMore);

            var action = Assert.Single(index.Entries, e => e.Kind == SearchKind.Action && e.Label.Key == "Settings_ControllerDatabaseUpdateNow");
            Assert.Equal(SearchPage.Settings, action.Page);
            Assert.Equal("Settings_ControllerDatabase", action.Qualifier?.Key);
            Assert.Equal("Settings_Updates", action.CardTitle?.Key);

            // The reset is read as an entry, not left out and not unnamed.
            int line = ExtrasPageTests.RepoText("PadForge.App", "Views", "SettingsPage.xaml").Split('\n')
                .Select((text, i) => (text, i)).First(l => l.text.Contains("CommandParameter=\"ControllerDatabaseStatus\"", StringComparison.Ordinal)).i + 1;
            Assert.Contains((SearchPage.Settings, line, ResetOutcome.Entry), index.Resets);
        }
    }
}
