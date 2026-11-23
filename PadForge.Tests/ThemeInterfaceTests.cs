using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PadForge.Common;
using PadForge.Common.Themes;
using PadForge.Controls;
using PadForge.ViewModels;
using PadForge.Views;
using PadForge.Views.Controls;
using Wpf.Ui.Appearance;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The theme interface (#591): the gallery's keys, the builder's search
    /// box, the copy of PadForge's own look, the profile note, the game look
    /// faults and the Community panel's game look line. They share the one
    /// WPF application of a test run with the other theme tests.
    /// </summary>
    [Collection("WpfApplicationSingleton")]
    public class ThemeInterfaceTests
    {
        private static readonly TimeSpan Limit = TimeSpan.FromMinutes(3);

        private static string TempFolder()
        {
            string folder = Path.Combine(Path.GetTempPath(), "PadForgeThemeInterfaceTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            return folder;
        }

        private static void Drop(string folder)
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static void Pump(Dispatcher dispatcher) => dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

        private static void BackToStock()
        {
            ThemeManager.SetPreview(null);
            ThemeManager.SetGameLook(null);
            ThemeManager.LoadRegistry(null);
            ThemeManager.Apply(2, null);
            PageRenderDump.ApplyTheme(ApplicationTheme.Dark);
            EmberTheme.ApplyAccent();
        }

        /// <summary>A window that is never shown: it gives the content a
        /// presentation source, so it loads and takes key events, and
        /// nothing reaches the screen.</summary>
        private static HwndSource Unshown(Visual content, int width = 900, int height = 600)
        {
            // WS_POPUP, without WS_VISIBLE.
            var source = new HwndSource(new HwndSourceParameters("PadForge test") { WindowStyle = unchecked((int)0x80000000), Width = width, Height = height });
            source.RootVisual = content;
            return source;
        }

        /// <summary>A key pressed on an element as the input system routes it:
        /// the tunneling event, then the bubbling one, which a tunneling
        /// handler that handled it leaves handled.</summary>
        private static bool Press(UIElement element, Key key, PresentationSource source = null)
        {
            source ??= PresentationSource.FromVisual(element);
            Assert.NotNull(source);
            using var none = HeldModifiers.Say(ModifierKeys.None);
            var preview = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            element.RaiseEvent(preview);
            var down = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.KeyDownEvent, Handled = preview.Handled };
            element.RaiseEvent(down);
            // A key down left unhandled reaches the access keys, the default
            // button's among them (wpf AccessKeyManager.cs:196).
            return down.Handled;
        }

        private static IEnumerable<T> Below<T>(DependencyObject root) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T hit) yield return hit;
                foreach (var deeper in Below<T>(child)) yield return deeper;
            }
        }

        private static ThemeDefinition Mine(string name) => new ThemeDefinition
        {
            Kind = ThemeKind.File, Name = name, Dark = false,
            Background = Color.FromRgb(0xF0, 0xF4, 0xFF), Accent = Color.FromRgb(0x11, 0x44, 0xCC),
            Secondary = Color.FromRgb(0x22, 0x77, 0x55), Attention = Color.FromRgb(0x88, 0x55, 0x00),
        };

        // ─────────────────────────────────────────────
        //  The gallery
        // ─────────────────────────────────────────────

        [Fact]
        public void TheGalleryLeavesEnterAndDeleteToTheTilesButtons()
        {
            string folder = TempFolder();
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                HwndSource window = null;
                try
                {
                    string kept = Path.Combine(folder, "kept.pftheme"), gone = Path.Combine(folder, "gone.pftheme");
                    ThemeFile.Write(kept, Mine("Kept"));
                    // A listed file that is not there: a file tile whose Edit does nothing.
                    ThemeManager.LoadRegistry(new[] { ("Gone", gone), ("Kept", kept) });
                    var vm = new SettingsViewModel();
                    var gallery = new ThemeGallery { DataContext = vm };
                    window = Unshown(gallery);
                    Pump(dispatcher);

                    ClickBorder[] ButtonsOf(string id)
                    {
                        var tile = ((List<ThemeTile>)gallery.Tiles.ItemsSource).Single(t => t.Id == id);
                        gallery.Tiles.SelectedItem = tile;
                        Pump(dispatcher);
                        var item = (ListBoxItem)gallery.Tiles.ItemContainerGenerator.ContainerFromItem(tile);
                        Assert.NotNull(item);
                        var buttons = Below<ClickBorder>(item).ToArray();
                        Assert.Equal(2, buttons.Length);
                        return buttons;
                    }

                    string keptId = ThemeManager.FilePrefix + kept, goneId = ThemeManager.FilePrefix + gone;

                    // Delete on the picked tile's Edit button takes nothing off the list.
                    Press(ButtonsOf(keptId)[0], Key.Delete);
                    Pump(dispatcher);
                    Assert.Equal(2, ThemeManager.Files.Count);

                    // Enter on the picked tile's Remove button takes it off the list.
                    Press(ButtonsOf(goneId)[1], Key.Enter);
                    Pump(dispatcher);
                    Assert.Equal(new[] { keptId }, ThemeManager.Files.Select(f => f.Id).ToArray());

                    // Delete on the tile itself still takes it off the list.
                    ButtonsOf(keptId);
                    var keptTile = ((List<ThemeTile>)gallery.Tiles.ItemsSource).Single(t => t.Id == keptId);
                    Press((ListBoxItem)gallery.Tiles.ItemContainerGenerator.ContainerFromItem(keptTile), Key.Delete);
                    Pump(dispatcher);
                    Assert.Empty(ThemeManager.Files);
                }
                finally
                {
                    window?.Dispose();
                    BackToStock();
                    Drop(folder);
                }
            }, Limit, "the gallery key test timed out");
        }

        [Fact]
        public void TheCopyOfPadForgesOwnLookHoldsThePickedLooksColors()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                try
                {
                    // Light picked, and a game look on the dark base over it.
                    ThemeManager.Apply(1, null);
                    ThemeManager.SetGameLook(new ThemeDefinition { Kind = ThemeKind.GameLook, Id = ThemeManager.GamePrefix + "620", Name = "Portal 2", Dark = true });
                    Assert.Equal(ApplicationTheme.Dark, ApplicationThemeManager.GetAppTheme());
                    var light = ThemeFile.Parse(ThemeGallery.CopyBytes(null, "Light"), "f");
                    Assert.False(light.Dark);
                    Assert.Equal(ThemeEngine.StockGround(false), light.Background);
                    Assert.Equal(ThemeEngine.StockSecondary(false), light.Secondary);
                    Assert.Equal(ThemeEngine.StockAttention(false), light.Attention);

                    // Dark picked under no other look: the dark ground.
                    ThemeManager.SetGameLook(null);
                    ThemeManager.Apply(2, null);
                    var dark = ThemeFile.Parse(ThemeGallery.CopyBytes(null, "Dark"), "f");
                    Assert.True(dark.Dark);
                    Assert.Equal(ThemeEngine.StockGround(true), dark.Background);
                }
                finally
                {
                    BackToStock();
                }
            }, Limit, "the copy test timed out");
        }

        [Fact]
        public void TheProfileNoteFollowsARenameOfTheProfileThatWearsTheLook()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                HwndSource window = null;
                try
                {
                    var vm = new SettingsViewModel { ActiveProfileInfo = "Old Name" };
                    var gallery = new ThemeGallery { DataContext = vm };
                    window = Unshown(gallery);
                    Pump(dispatcher);
                    ThemeManager.SetGameLook(new ThemeDefinition { Kind = ThemeKind.GameLook, Id = ThemeManager.GamePrefix + "620", Name = "Portal 2", Dark = true });
                    Pump(dispatcher);
                    Assert.Contains("Old Name", gallery.Note.Text);

                    // The profile is renamed while its look stays on.
                    vm.ActiveProfileInfo = "New Name";
                    Pump(dispatcher);
                    Assert.Contains("New Name", gallery.Note.Text);
                    Assert.DoesNotContain("Old Name", gallery.Note.Text);
                }
                finally
                {
                    window?.Dispose();
                    BackToStock();
                }
            }, Limit, "the profile note test timed out");
        }

        [Fact]
        public void AFileThatCannotBeReadIsToldApartFromOneThatIsNoTheme()
        {
            string folder = TempFolder();
            try
            {
                string locked = Path.Combine(folder, "locked.pftheme"), junk = Path.Combine(folder, "junk.pftheme");
                ThemeFile.Write(locked, Mine("Locked"));
                File.WriteAllText(junk, "not a theme");
                Assert.Equal(ThemeFault.None, ThemeFile.Read(locked).Fault);
                ThemeFault held;
                // Another program holds the file and shares nothing.
                using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    held = ThemeFile.Read(locked).Fault;
                    Assert.Null(ThemeManager.Register(locked, out ThemeFault registered));
                    Assert.Equal(ThemeFault.Unreadable, registered);
                }
                Assert.Equal(ThemeFault.Unreadable, held);
                Assert.NotEqual(held, ThemeFile.Read(junk).Fault);
                Assert.Null(ThemeManager.Register(junk, out ThemeFault junkFault));
                Assert.Equal(ThemeFault.NotATheme, junkFault);
                Assert.Null(ThemeManager.Register(Path.Combine(folder, "none.pftheme"), out ThemeFault noneFault));
                Assert.Equal(ThemeFault.Missing, noneFault);
                Assert.Empty(ThemeManager.Files);

                // Add Theme says which: only a file that is no theme is called one.
                var s = PadForge.Resources.Strings.Strings.Instance;
                Assert.Equal(s.Settings_ThemeAddFailed, ThemeGallery.AddFailedText(ThemeFault.NotATheme));
                Assert.Equal(s.Settings_ThemeAddUnreadable, ThemeGallery.AddFailedText(ThemeFault.Unreadable));
                Assert.Equal(s.Settings_ThemeAddMissing, ThemeGallery.AddFailedText(ThemeFault.Missing));
            }
            finally
            {
                Drop(folder);
            }
        }

        [Fact]
        public void TheNoteUnderAPickedFileThatIsNoThemeSaysSo()
        {
            var s = PadForge.Resources.Strings.Strings.Instance;
            ThemeDefinition Faulted(ThemeFault fault) => new ThemeDefinition { Kind = ThemeKind.File, Name = "Mine", Fault = fault };
            var highContrast = ThemeManager.HighContrastOn;
            try
            {
                ThemeManager.HighContrastOn = () => false;
                string notATheme = ThemeGallery.FaultNoteText(Faulted(ThemeFault.NotATheme));
                Assert.Equal(string.Format(s.Settings_ThemeNotAThemeNote_Format, "Mine"), notATheme);
                Assert.NotEqual(string.Format(s.Settings_ThemeFaultNote_Format, "Mine"), notATheme);
                Assert.Equal(string.Format(s.Settings_ThemeFaultNote_Format, "Mine"), ThemeGallery.FaultNoteText(Faulted(ThemeFault.Missing)));
                Assert.Equal(string.Format(s.Settings_ThemeFaultNote_Format, "Mine"), ThemeGallery.FaultNoteText(Faulted(ThemeFault.Unreadable)));
                Assert.Null(ThemeGallery.FaultNoteText(Faulted(ThemeFault.None)));
                Assert.Null(ThemeGallery.FaultNoteText(null));
            }
            finally
            {
                ThemeManager.HighContrastOn = highContrast;
            }
        }

        /// <summary>Under Windows' high contrast the look on screen is the
        /// contrast theme whatever is picked, so a picked file that is gone,
        /// unreadable or no theme gets no note: its tile names the fault, and
        /// the note's line about Dark would be false. Outside high contrast
        /// the note is there, as PadForge shows Dark.</summary>
        [Fact]
        public void UnderHighContrastAFaultedFileGetsNoNoteAboutDark()
        {
            var s = PadForge.Resources.Strings.Strings.Instance;
            ThemeDefinition Faulted(ThemeFault fault) => new ThemeDefinition { Kind = ThemeKind.File, Name = "Mine", Fault = fault };
            var highContrast = ThemeManager.HighContrastOn;
            try
            {
                ThemeManager.HighContrastOn = () => false;
                Assert.Equal(string.Format(s.Settings_ThemeFaultNote_Format, "Mine"), ThemeGallery.FaultNoteText(Faulted(ThemeFault.Missing)));
                Assert.Equal(string.Format(s.Settings_ThemeNotAThemeNote_Format, "Mine"), ThemeGallery.FaultNoteText(Faulted(ThemeFault.NotATheme)));

                ThemeManager.HighContrastOn = () => true;
                foreach (ThemeFault fault in new[] { ThemeFault.Missing, ThemeFault.Unreadable, ThemeFault.NotATheme })
                    Assert.Null(ThemeGallery.FaultNoteText(Faulted(fault)));
            }
            finally
            {
                ThemeManager.HighContrastOn = highContrast;
            }
        }

        // ─────────────────────────────────────────────
        //  A game look's faults
        // ─────────────────────────────────────────────

        [Fact]
        public void ALookSteamCouldNotBeReachedForIsNotALookSteamHasNoPictureFor()
        {
            string folder = TempFolder();
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                try
                {
                    // The switch on and nothing in the cache. The test host's
                    // Steam client answers as an unreachable server answers.
                    var art = new SteamGameArt(() => true, new PadForge.SteamWorkshop.Cache.SteamWorkshopCache(Path.Combine(folder, "cache")));
                    var task = GameLooks.MakeAsync(art, 620, "Portal 2", System.Threading.CancellationToken.None);
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    while (!task.IsCompleted)
                    {
                        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), "the look was not made");
                        Pump(dispatcher);
                        System.Threading.Thread.Sleep(10);
                    }
                    var made = task.GetAwaiter().GetResult();
                    Assert.Null(made.Look);
                    Assert.NotEqual(GameLookFault.NoArt, made.Fault);
                    Assert.Equal(GameLookFault.Unreached, made.Fault);
                    Assert.Equal(PadForge.Resources.Strings.Strings.Instance.ThemeBuilder_SearchFailed, GameLooks.FaultText(made.Fault));
                    Assert.Equal(PadForge.Resources.Strings.Strings.Instance.ThemeBuilder_NoArt, GameLooks.FaultText(GameLooks.FaultOfNoPicture(true, false)));
                    Assert.Equal(GameLookFault.LookupOff, GameLooks.FaultOfNoPicture(false, true));
                }
                finally
                {
                    BackToStock();
                    Drop(folder);
                }
            }, Limit, "the unreachable look test timed out");
        }

        // ─────────────────────────────────────────────
        //  The builder
        // ─────────────────────────────────────────────

        [Fact]
        public void EnterInTheGameSearchSearchesAndSavesNothing()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                HwndSource keys = null;
                try
                {
                    // The switch off: the search asks nothing of the network and finds nothing.
                    var dialog = new ThemeBuilderDialog(null, new SettingsViewModel { EnableCommunityConfigLookup = false });
                    keys = Unshown(new Border(), 10, 10);
                    dialog.GameSearchBox.Text = "portal";
                    Assert.True(Press(dialog.GameSearchBox, Key.Enter, keys), "Enter in the search box reached the Save button");
                    Pump(dispatcher);
                    Assert.Equal(PadForge.Resources.Strings.Strings.Instance.Workshop_NoGamesFound, dialog.GameStatus.Text);
                    // Enter in the list of games is the list's too.
                    Assert.True(Press(dialog.GameList, Key.Enter, keys), "Enter in the game list reached the Save button");
                    dialog.Close();
                    Assert.Null(dialog.Saved);
                }
                finally
                {
                    keys?.Dispose();
                    BackToStock();
                }
            }, Limit, "the builder search test timed out");
        }

        private static string RepoRoot()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root != null && !Directory.Exists(Path.Combine(root.FullName, "PadForge.App"))) root = root.Parent;
            Assert.NotNull(root);
            return root.FullName;
        }

        [Theory]
        [InlineData("ProfileDialog.xaml", "GameLookSwitch", "Profile_GameLook")]
        [InlineData("ThemeBuilderDialog.xaml", "PositionSlider", "ThemeBuilder_Position")]
        [InlineData("ThemeBuilderDialog.xaml", "LogoSwitch", "ThemeBuilder_ShowLogo")]
        public void EveryThemeSettingInADialogHasItsReset(string file, string control, string label)
        {
            string xaml = File.ReadAllText(Path.Combine(RepoRoot(), "PadForge.App", "Views", file));
            int at = xaml.IndexOf("x:Name=\"" + control + "\"", StringComparison.Ordinal);
            Assert.True(at >= 0, control + " is not in " + file);
            int rowEnd = xaml.IndexOf("</controls:SettingRow>", at, StringComparison.Ordinal);
            Assert.True(rowEnd > at, control + " sits in no SettingRow");
            string rest = xaml.Substring(at, rowEnd - at);
            var reset = System.Text.RegularExpressions.Regex.Match(rest, @"<reset:SettingResetButton\b[^>]*>");
            Assert.True(reset.Success, control + " in " + file + " has no reset after it");
            Assert.Contains("SettingLabel=\"{Binding " + label + ",", reset.Value);
        }

        [Fact]
        public void TheBuildersResetsPutThePicturesDefaultsBack()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                try
                {
                    var dialog = new ThemeBuilderDialog(null, new SettingsViewModel { EnableCommunityConfigLookup = false });
                    Reset ResetIn(FrameworkElement row) => new Reset(Below<SettingResetButton>(row).Single());
                    dialog.PositionSlider.Value = 20;
                    ResetIn(dialog.PositionRow).Click();
                    Assert.Equal(50, dialog.PositionSlider.Value);
                    dialog.LogoSwitch.IsChecked = false;
                    ResetIn(dialog.LogoRow).Click();
                    Assert.True(dialog.LogoSwitch.IsChecked);
                    dialog.Close();
                }
                finally
                {
                    BackToStock();
                }
            }, Limit, "the builder reset test timed out");
        }

        private sealed class Reset
        {
            private readonly SettingResetButton _button;
            internal Reset(SettingResetButton button) => _button = button;
            internal void Click() => _button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, _button));
        }

        // ─────────────────────────────────────────────
        //  The Community panel
        // ─────────────────────────────────────────────

        [Fact]
        public void TheGameLookLineLeavesWithItsSwitch()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                const System.Reflection.BindingFlags hidden = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                var type = typeof(PadForge.Views.CommunityConfigsPanel);
                var panel = new PadForge.Views.CommunityConfigsPanel();
                type.GetField("_selectedGame", hidden).SetValue(panel, new PadForge.Views.WorkshopGameItem { AppId = 620, Name = "Portal 2" });
                void Fill(string name) => type.GetMethod("SetManifestPanel", hidden).Invoke(panel, new[] { panel.FindName(name) });
                var group = (FrameworkElement)panel.FindName("GameLookGroup");
                var status = (TextBlock)panel.FindName("GameLookStatus");

                Fill("ManifestResultPanel");
                Assert.Equal(Visibility.Visible, group.Visibility);
                type.GetMethod("DropGameLook", hidden).Invoke(panel, new object[] { "Why it went off" });
                Assert.Equal(Visibility.Visible, status.Visibility);

                // Another config of the same game loads: the switch goes, and its line with it.
                Fill("ManifestLoadingPanel");
                Assert.Equal(Visibility.Collapsed, group.Visibility);
                Assert.Equal(Visibility.Collapsed, status.Visibility);
                Fill("ManifestErrorPanel");
                Assert.Equal(Visibility.Collapsed, status.Visibility);
            }, Limit, "the game look line test timed out");
        }
    }
}
