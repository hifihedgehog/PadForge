using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using PadForge.Controls;
using PadForge.Search;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Search and the open mapping row. The index reads the row as the page
    /// builds it for the row's first source: the row's template, and each
    /// part a template of its own draws, in its place and on the data the
    /// part gives it. A landing then looks for an option's label in the
    /// row's body, beside the row's line and its added sources' lines, which
    /// say the same words.
    /// </summary>
    [Collection("WpfApplicationSingleton")]
    public class SearchOpenRowTests
    {
        private static SearchIndex Index() => SearchIndexBuilder.Read(
            SearchSources.FileNames.ToDictionary(p => p.Key, p => ExtrasPageTests.RepoText("PadForge.App", "Views", p.Value)),
            PadPage.TabOfGroupName);

        private static List<SearchEntry> Options(IEnumerable<SearchEntry> index) => index.Where(e => e.RowOptions).ToList();

        private static void Sta(Action action)
        {
            Exception failure = null;
            var thread = new Thread(() => { try { action(); } catch (Exception e) { failure = e; } }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(20000), "the run timed out");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }

        /// <summary>The open row's options stand in the index in the order
        /// their elements stand in the row: a source's own parts, what
        /// stands under it (its mode's settings, its deadzone, its input's
        /// dials), then the output. Each is read once, on the row's first
        /// source, under the rule of every part on the way to it.</summary>
        [Fact]
        public void TheOpenRowIsReadInItsElementsOrder_EachOptionOnce()
        {
            var index = Index();
            var options = Options(index.Entries);
            var keys = options.Select(e => e.Label.Key).ToList();
            foreach (string once in new[]
                     {
                         "Pad_Row_Mode", "ResetLabel_UpInput", "ResetLabel_DownInput", "ResetLabel_ModifierInput", "Pad_Invert", "Pad_Half", "Pad_Either",
                         "Pad_Mapping_RapidTrigger_Distance", "Pad_Mapping_Rate", "Pad_Mapping_Sticky", "Pad_Mapping_Min", "Pad_Mapping_Max",
                         "Pad_Mapping_Ramp_Attack", "Pad_Mapping_Ramp_Release", "Pad_Mapping_Ramp_Reverse", "Pad_Mapping_Ramp_Autocenter",
                         "Pad_HeaderDeadZone", "Pad_InvertOutput", "Pad_Mapping_Accel", "Mapping_MouseCursorSensitivity",
                         "Pad_Combine_Label", "Pad_Shift_NoInherit", "Pad_Mapping_TrimDeadzone", "Pad_Mapping_TrimRate", "Pad_Mapping_TrimReset",
                         "Pad_Mapping_MotionMode", "Pad_Mapping_MotionTopSpeed", "Pad_Mapping_MotionStartSpeed", "Pad_Mapping_MotionLeanAngle",
                         "Pad_Mapping_MotionDeadzone", "ResetLabel_Formula",
                     })
                Assert.True(keys.Count(k => k == once) == 1, once + " is read " + keys.Count(k => k == once) + " times");
            // A gyro's rate and a lean's are two settings under one name, and
            // so are the pointer's and the mouse's.
            Assert.Equal(2, keys.Count(k => k == "Mapping_GyroSensitivity"));
            Assert.Equal(2, keys.Count(k => k == "Mapping_IrPointerSensitivity"));

            int At(string key) => keys.IndexOf(key);
            string[] order =
            {
                "Pad_Row_Mode", "ResetLabel_UpInput", "ResetLabel_DownInput", "ResetLabel_ModifierInput", "Pad_Invert", "Pad_Half", "Pad_Either",
                "Pad_Mapping_RapidTrigger_Distance", "Pad_Mapping_Ramp_Autocenter", "Pad_HeaderDeadZone", "Pad_InvertOutput", "Mapping_IrPointerSensitivity",
                "Pad_Combine_Label", "Pad_Shift_NoInherit", "Pad_Mapping_TrimDeadzone", "Pad_Mapping_MotionMode", "Pad_Mapping_MotionDeadzone", "ResetLabel_Formula",
            };
            for (int i = 1; i < order.Length; i++)
                Assert.True(At(order[i - 1]) < At(order[i]), order[i - 1] + " stands before " + order[i] + " in the row");
            Assert.Equal(options.Select(e => e.Order).OrderBy(o => o), options.Select(e => e.Order));

            // The rules on the way to an option, through each part's data.
            string[] Gates(string key) => options.First(e => e.Label.Key == key).ItemGates.Select(g => g.Path + (g.ConverterParameter == "Invert" ? " (not)" : "")).ToArray();
            Assert.Empty(Gates("Pad_Row_Mode"));
            Assert.Equal(new[] { "IsLineKeyMode", "ModeHolder.UsesUpDownKeys" }, Gates("ResetLabel_UpInput"));
            Assert.Equal(new[] { "IsLineKeyMode", "ModeHolder.IsInvertOnHoldKind" }, Gates("ResetLabel_ModifierInput"));
            Assert.Equal(new[] { "IsLineKeyMode (not)" }, Gates("Pad_Invert"));
            Assert.Equal(new[] { "IsLineKeyMode (not)" }, Gates("Pad_Either"));
            Assert.Equal(new[] { "ModeHolder.IsRampedKind" }, Gates("Pad_Mapping_Ramp_Attack"));
            Assert.Equal(new[] { "ModeHolder.IsIncrementalKind" }, Gates("Pad_Mapping_Sticky"));
            Assert.Equal(new[] { "ShowsLineDeadZone" }, Gates("Pad_HeaderDeadZone"));
            Assert.Equal(new[] { "IsLineKeyMode (not)", "IsParamAccelApplicable" }, Gates("Pad_Mapping_Accel"));
            Assert.Equal(new[] { "IsMultiSource" }, Gates("Pad_Combine_Label"));
            Assert.Equal(new[] { "ShouldShowTrimSettings" }, Gates("Pad_Mapping_TrimRate"));
            Assert.Equal(new[] { "ShouldShowCustomExpression" }, Gates("ResetLabel_Formula"));

            // What a result's element is known by. A switch of the line by
            // its own words: its name shows in a narrow box only, and the
            // head over the sources says the same word. Mode by its reset.
            SearchLabelSource Source(string key) => options.First(e => e.Label.Key == key).LabelSource;
            Assert.All(new[] { "Pad_Invert", "Pad_Half", "Pad_Either" }, key => Assert.Equal(SearchLabelSource.Toggle, Source(key)));
            // A key by its reset too, which says its whole name: the line
            // has only "Up" before the picker.
            Assert.All(new[] { "Pad_Row_Mode", "ResetLabel_Formula", "ResetLabel_UpInput", "ResetLabel_DownInput", "ResetLabel_ModifierInput" },
                key => Assert.Equal(SearchLabelSource.ResetLabel, Source(key)));
            Assert.All(new[] { "Pad_Mapping_Sticky", "Pad_HeaderDeadZone", "Pad_Combine_Label" }, key => Assert.Equal(SearchLabelSource.Text, Source(key)));
            Assert.DoesNotContain(keys, k => k is "Pad_Row_Up" or "Pad_Row_Down" or "Pad_Row_Modifier");
            var s = PadForge.Resources.Strings.Strings.Instance;
            Assert.Equal(new[] { "Up Input", "Down Input", "Modifier Input" }, new[] { s.ResetLabel_UpInput, s.ResetLabel_DownInput, s.ResetLabel_ModifierInput });
            // A switch's value is read on the row, as the row's first source.
            Assert.Equal("LineInvert", options.First(e => e.Label.Key == "Pad_Invert").Value?.Path);
            Assert.Equal("ModeHolder.ParamSticky", options.First(e => e.Label.Key == "Pad_Mapping_Sticky").Value?.Path);

            // Every reset of the open row's templates is an entry, read once.
            var page = System.Xml.Linq.XDocument.Parse(ExtrasPageTests.RepoText("PadForge.App", "Views", "PadPage.xaml"), System.Xml.Linq.LoadOptions.SetLineInfo);
            System.Xml.Linq.XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var lines = new List<int>();
            foreach (string key in new[] { SearchIndexBuilder.RowTemplate, "SourceLine", "SourceModeSettings", "SourceDials" })
            {
                var template = page.Descendants().Single(e => e.Name.LocalName == "DataTemplate" && (string)e.Attribute(x + "Key") == key);
                lines.AddRange(template.Descendants()
                    .Where(e => e.Name.LocalName == "SettingResetButton"
                        || (e.Name.LocalName == "Button" && (string)e.Attribute("Style") == "{StaticResource ResetButton}"))
                    .Select(e => ((System.Xml.IXmlLineInfo)e).LineNumber));
            }
            Assert.Equal(options.Count, lines.Count);
            foreach (int line in lines)
                Assert.Equal(ResetOutcome.Entry, Assert.Single(index.Resets, r => r.Page == SearchPage.Pad && r.Line == line).Outcome);
        }

        /// <summary>A landing looks for a label among the parts of the
        /// row's body. The row's line is left out, and so is the list of
        /// the row's added sources, each with all that is in it.</summary>
        [Fact]
        public void ALandingLooksInTheRowsBody_BesideItsLineAndItsAddedSources() => Sta(() =>
        {
            TextBlock Word(string text) => new TextBlock { Text = text };
            var tag = Word("Up");
            var line = new Grid { Name = SearchNavigator.RowLineName };
            line.Children.Add(new StackPanel { Children = { tag } });
            var first = Word("Up");
            var addedWord = Word("Up");
            var added = new StackPanel { Name = SearchNavigator.AddedSourcesName };
            added.Children.Add(new Border { Child = addedWord });
            var output = Word("Combine");
            var body = new StackPanel();
            body.Children.Add(new Border { Child = first });
            body.Children.Add(added);
            body.Children.Add(output);
            var row = new StackPanel();
            row.Children.Add(line);
            row.Children.Add(body);

            var parts = SearchNavigator.RowOptionParts(row).ToList();
            Assert.Contains(first, parts);
            Assert.Contains(output, parts);
            Assert.Contains(body, parts);
            Assert.DoesNotContain(line, parts);
            Assert.DoesNotContain(tag, parts);
            Assert.DoesNotContain(added, parts);
            Assert.DoesNotContain(addedWord, parts);
            // In the row's order, so the Nth entry of a word is its Nth element.
            Assert.True(parts.IndexOf(first) < parts.IndexOf(output));
            Assert.Equal(new[] { first }, parts.OfType<TextBlock>().Where(t => t.Text == "Up"));
            Assert.Equal(("RowLine", "AddedSources"), (SearchNavigator.RowLineName, SearchNavigator.AddedSourcesName));
            // The page names the two so.
            string page = ExtrasPageTests.RepoText("PadForge.App", "Views", "PadPage.xaml");
            Assert.Contains("<Grid x:Name=\"RowLine\"", page);
            Assert.Contains("<ItemsControl x:Name=\"AddedSources\" ItemsSource=\"{Binding ExtraSources}\" ItemTemplate=\"{StaticResource SourceLine}\"", page);
            Assert.Contains("var parts = RowOptionParts(row).ToList();", ExtrasPageTests.RepoText("PadForge.App", "Search", "SearchNavigator.cs"));
        });

        /// <summary>On the page itself: a row of three sources is opened,
        /// and each option of the index is found on the row's first source
        /// or its output. Invert lands on the first source's switch, where
        /// the head's word and the next source's switch say Invert too. The
        /// row's line and the added sources' lines are no part of what is
        /// searched.</summary>
        [Fact]
        public void AnOptionLandsOnTheFirstSourcesPart()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                var vm = new PadViewModel(0);
                var pad = Guid.NewGuid();
                string id = pad.ToString("D");
                vm.MappedDevices.Add(new PadViewModel.MappedDeviceInfo { InstanceGuid = pad, Name = "Pad", TypeKey = "Gamepad", IsOnline = true });
                var row = vm.Mappings.First(m => m.TargetSettingName == "RightTrigger");
                row.PrimarySourceDeviceGuid = id;
                row.PrimarySourceDeviceLabel = "Pad";
                row.LoadDescriptor("Axis 5");
                var steps = new MappingSourceItem { Kind = "Incremental", DeviceGuid = id, DeviceLabel = "Pad" };
                row.ExtraSources.Add(steps);
                steps.SetParamKey(MappingSourceItem.ParamRecordTarget.Up, "Button 4", id);
                steps.SetParamKey(MappingSourceItem.ParamRecordTarget.Down, "Button 5", id);
                row.ExtraSources.Add(new MappingSourceItem { Kind = "Direct", DeviceGuid = id, DeviceLabel = "Pad", Descriptor = "Axis 2" });

                var page = new PadPage { DataContext = vm };
                page.BindSteamVrState(new SettingsViewModel { IsSteamVrInstalled = true });
                vm.SelectedConfigTab = PadViewModel.MappingsTabIndex;
                page.Measure(new Size(1456, 1500));
                page.Arrange(new Rect(0, 0, 1456, 1500));
                page.UpdateLayout();
                var grid = (DataGrid)page.FindName("MappingDataGrid");
                grid.SelectedItem = row;
                grid.ScrollIntoView(row);
                page.UpdateLayout();
                var container = grid.ItemContainerGenerator.ContainerFromItem(row) as DataGridRow;
                Assert.NotNull(container);
                container.DetailsVisibility = Visibility.Visible;
                container.UpdateLayout();

                var index = Index().Entries;
                var options = Options(index);
                var whole = SearchLocator.VisualDescendants(container).ToList();
                var parts = SearchNavigator.RowOptionParts(container).ToList();
                var line = whole.OfType<Grid>().Single(g => g.Name == SearchNavigator.RowLineName);
                var added = whole.OfType<ItemsControl>().Single(i => i.Name == SearchNavigator.AddedSourcesName);
                var first = parts.OfType<ContentControl>().Single(c => c.Name == "FirstSource");
                var output = parts.OfType<Border>().Single(b => b.Name == "OutputZone");
                bool Under(DependencyObject d, DependencyObject top)
                {
                    for (var at = d; at != null; at = System.Windows.Media.VisualTreeHelper.GetParent(at))
                        if (ReferenceEquals(at, top)) return true;
                    return false;
                }
                // The row shows what the test is about: three lines, and a
                // tag in the row's line that says Up.
                Assert.Equal(2, added.Items.Count);
                string up = PadForge.Resources.Strings.Strings.Instance.Pad_Row_Up;
                Assert.Contains(whole.OfType<TextBlock>(), t => t.Text == up && Under(t, line));
                Assert.Contains(whole.OfType<TextBlock>(), t => t.Text == up && Under(t, added));

                DependencyObject Find(SearchEntry entry) => SearchLocator.FindAmong(parts, entry, row, index, e => e.RowOptions);
                SearchEntry Entry(string key) => options.First(e => e.Label.Key == key);
                string Target(SearchEntry entry) => SearchLocator.TargetOf(Find(entry), entry)?.Name;

                // Every option of the index has its element, on the first
                // source or the output, and no two have one element.
                var found = options.Select(e => (Entry: e, Element: Find(e))).ToList();
                Assert.All(found, f => Assert.True(f.Element != null, f.Entry.Label.Key + " has no element in the open row"));
                Assert.All(found, f => Assert.True(Under(f.Element, first) || Under(f.Element, output), f.Entry.Label.Key + " lands outside the first source and the output"));
                Assert.Equal(found.Count, found.Select(f => f.Element).Distinct(ReferenceEqualityComparer.Instance).Count());

                // Invert: the first source's switch.
                var invert = Find(Entry("Pad_Invert"));
                Assert.IsType<CheckBox>(invert);
                Assert.True(Under(invert, first));
                Assert.Equal("InvertPart", Target(Entry("Pad_Invert")));
                Assert.Equal("HalfPart", Target(Entry("Pad_Half")));
                Assert.Equal("EitherPart", Target(Entry("Pad_Either")));
                // Mode: the first source's reset, in the Mode part.
                Assert.IsType<SettingResetButton>(Find(Entry("Pad_Row_Mode")));
                Assert.True(Under(Find(Entry("Pad_Row_Mode")), first));
                // A key: the first source's, by its reset.
                Assert.IsType<SettingResetButton>(Find(Entry("ResetLabel_UpInput")));
                Assert.True(Under(Find(Entry("ResetLabel_UpInput")), first));
                Assert.Equal("DeadzoneGroup", Target(Entry("Pad_HeaderDeadZone")));
                Assert.True(Under(Find(Entry("Pad_Combine_Label")), output));

                // The row says Up Input once for each of its three sources,
                // and the parts that are searched say it once.
                string upInput = PadForge.Resources.Strings.Strings.Instance.ResetLabel_UpInput;
                Assert.Equal(3, whole.OfType<SettingResetButton>().Count(r => r.SettingLabel == upInput));
                Assert.Equal(1, parts.OfType<SettingResetButton>().Count(r => r.SettingLabel == upInput));
                Assert.DoesNotContain(parts, p => Under(p, line) || Under(p, added));

                // The row that shows an option is the one its rules pick.
                bool Passes(SearchEntry entry, object item) => entry.ItemGates.All(g => BindingProbe.Passes(item, g, k => page.TryFindResource(k)));
                var button = vm.Mappings.First(m => m.TargetSettingName == "ButtonA");
                Assert.True(Passes(Entry("Pad_Invert"), row));
                Assert.True(Passes(Entry("Pad_Combine_Label"), row));
                Assert.False(Passes(Entry("Pad_Combine_Label"), button));
                Assert.False(Passes(Entry("Pad_Mapping_Ramp_Attack"), row));
                Assert.False(Passes(Entry("ResetLabel_UpInput"), row));
                button.PrimaryKindSource.Kind = "Ramped";
                Assert.True(Passes(Entry("Pad_Mapping_Ramp_Attack"), button));
                Assert.True(Passes(Entry("ResetLabel_UpInput"), button));
                Assert.False(Passes(Entry("Pad_Invert"), button));
                Assert.True(Passes(Entry("Pad_Combine_Label"), button));
            }, TimeSpan.FromMinutes(3), "the open row's search run timed out");
        }
    }
}
