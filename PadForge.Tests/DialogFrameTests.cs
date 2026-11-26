using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Xml.Linq;
using PadForge.Common;
using PadForge.Views;
using Xunit;
using Strings = PadForge.Resources.Strings.Strings;

namespace PadForge.Tests
{
    /// <summary>
    /// The frame every PadForge dialog is (#574): a title, a line under it,
    /// the dialog's own content and a foot of buttons, with the Standard and
    /// Wide measures of the dialog boards, and the dictionary of the parts
    /// the boards repeat.
    /// </summary>
    [Collection("WpfApplicationSingleton")]
    public class DialogFrameTests
    {
        /// <summary>A frame whose X reports the ask instead of closing a
        /// window that was never shown.</summary>
        private sealed class Probe : DialogWindow
        {
            internal int CloseAsked;
            protected override void OnCloseRequested() => CloseAsked++;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct WindowRect { public int Left, Top, Right, Bottom; }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out WindowRect rect);

        /// <summary>A dialog a test shows lies off every monitor, where no
        /// click on the desktop reaches it: centered on an owner off screen,
        /// or on the screen with no owner, Windows would put it in a
        /// monitor's work area, in front of the user's windows. The test
        /// host's fence (DialogWindow.OffLimitsForTest) keeps it off, modal
        /// or not.</summary>
        [Fact]
        public void ADialogATestShows_LiesOffEveryMonitor()
        {
            DialogTestHost.Run(() =>
            {
                var owner = new Window
                {
                    Width = 400, Height = 300, Left = -10000, Top = -10000,
                    ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
                };
                owner.Show();
                try
                {
                    string Where(Window shown)
                    {
                        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(shown).Handle;
                        GetWindowRect(hwnd, out var r);
                        // MONITOR_DEFAULTTONULL: no monitor holds any of it.
                        return MonitorFromWindow(hwnd, 0) == IntPtr.Zero ? null : r.Left + "," + r.Top + " to " + r.Right + "," + r.Bottom;
                    }

                    var centered = new DialogWindow { Owner = owner, Left = -20000, Top = -20000, ShowActivated = false };
                    centered.Show();
                    string onOwner = Where(centered);
                    centered.Close();
                    Assert.True(onOwner == null, "a dialog centered on its owner sits on a monitor at " + onOwner);

                    var alone = new DialogWindow { WindowStartupLocation = WindowStartupLocation.CenterScreen, ShowActivated = false };
                    alone.Show();
                    string onScreen = Where(alone);
                    alone.Close();
                    Assert.True(onScreen == null, "a dialog centered on the screen sits on a monitor at " + onScreen);

                    var modal = new DialogWindow { Owner = owner, ShowActivated = false };
                    string asDialog = "never shown";
                    modal.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() =>
                    {
                        asDialog = Where(modal);
                        modal.Close();
                    }));
                    modal.ShowDialog();
                    Assert.True(asDialog == null, "a modal dialog sits on a monitor at " + asDialog);
                }
                finally
                {
                    owner.Close();
                }
            });
        }

        [Fact]
        public void TheFrameShowsItsParts()
        {
            DialogTestHost.Run(() =>
            {
                var foot = new Button();
                var dialog = new Probe { Title = "T", Subtitle = "S", Foot = foot };
                dialog.ApplyTemplate();
                Assert.Equal("T", dialog.TitlePart.Text);
                var subtitle = (ContentPresenter)dialog.Template.FindName("PART_Subtitle", dialog);
                var footPart = (FrameworkElement)dialog.Template.FindName("PART_Foot", dialog);
                Assert.Equal(Visibility.Visible, subtitle.Visibility);
                Assert.Equal("S", subtitle.Content);
                Assert.Equal(Visibility.Visible, footPart.Visibility);

                dialog.Subtitle = null;
                Assert.Equal(Visibility.Collapsed, subtitle.Visibility);
                dialog.Subtitle = "";
                Assert.Equal(Visibility.Collapsed, subtitle.Visibility);
                dialog.Foot = null;
                Assert.Equal(Visibility.Collapsed, footPart.Visibility);

                Assert.NotNull(dialog.ClosePart);
                Assert.Equal(Visibility.Collapsed, dialog.ClosePart.Visibility);
                dialog.ShowClose = true;
                Assert.Equal(Visibility.Visible, dialog.ClosePart.Visibility);
                dialog.ClosePart.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Equal(1, dialog.CloseAsked);
            });
        }

        /// <summary>A test may make one dialog after another in one host.
        /// A stand-in Application that shut down when its last window closed
        /// took WPF's resource package with it: the next dialog could not
        /// load its markup, and the test after that ended the whole run
        /// (seen 2026-11-12, the first test to close two dialogs that had a
        /// handle). So the host's Application shuts down only when asked.</summary>
        [Fact]
        public void TheHostOutlivesADialogItClosed()
        {
            DialogTestHost.Run(() =>
            {
                for (int i = 0; i < 2; i++)
                {
                    var dialog = new DialogWindow { Title = "T" };
                    new System.Windows.Interop.WindowInteropHelper(dialog).EnsureHandle();
                    dialog.Close();
                    // What the close queued runs here, as it does when the
                    // next dialog is laid out.
                    dialog.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                }
                // A dialog that loads markup of its own still loads.
                var loaded = new ChatCommandsDialog();
                Assert.NotNull(loaded.FindName("WordBox"));
                loaded.Close();
                Assert.Equal(ShutdownMode.OnExplicitShutdown, Application.Current.ShutdownMode);
            });
        }

        [Fact]
        public void TheWideHeadTakesItsMeasures()
        {
            DialogTestHost.Run(() =>
            {
                var dialog = new DialogWindow();
                dialog.ApplyTemplate();
                Assert.Equal(560, dialog.Width);
                Assert.Equal(new Thickness(22, 20, 22, 0), dialog.HeadPadding);
                Assert.Equal(new Thickness(22, 14, 22, 16), dialog.BodyPadding);
                Assert.Equal(new Thickness(22, 14, 22, 14), dialog.FootPadding);
                Assert.Equal(18, dialog.TitlePart.FontSize);
                Assert.Equal(0, dialog.MinWidth);
                Assert.Equal(0, dialog.MinHeight);
                Assert.Equal(SizeToContent.Height, dialog.SizeToContent);
                Assert.Equal(ResizeMode.NoResize, dialog.ResizeMode);
                Assert.False(dialog.ShowInTaskbar);
                Assert.Equal(Wpf.Ui.Controls.WindowBackdropType.Mica, dialog.WindowBackdropType);
                Assert.True(dialog.ExtendsContentIntoTitleBar);
                Assert.Equal(WindowStartupLocation.CenterOwner, dialog.WindowStartupLocation);

                dialog.IsWide = true;
                Assert.Equal(680, dialog.Width);
                Assert.Equal(new Thickness(24, 22, 24, 0), dialog.HeadPadding);
                Assert.Equal(new Thickness(0), dialog.BodyPadding);
                Assert.Equal(new Thickness(24, 16, 24, 16), dialog.FootPadding);
                Assert.Equal(20, dialog.TitlePart.FontSize);
            });
        }

        [Fact]
        public void SubtitleAndFootAreLogicalChildren()
        {
            DialogTestHost.Run(() =>
            {
                var subtitle = new TextBlock { Text = "S" };
                var save = new Button();
                var foot = new StackPanel();
                foot.Children.Add(save);
                var dialog = new DialogWindow { Subtitle = subtitle, Foot = foot, Content = new Grid() };
                var children = LogicalTreeHelper.GetChildren(dialog).Cast<object>().ToList();
                Assert.Contains(subtitle, children);
                Assert.Contains(foot, children);
                Assert.Same(foot, LogicalTreeHelper.GetParent(save));
                Assert.Same(dialog, LogicalTreeHelper.GetParent(foot));
                Assert.Same(dialog, LogicalTreeHelper.GetParent(subtitle));

                dialog.Foot = null;
                Assert.DoesNotContain(foot, LogicalTreeHelper.GetChildren(dialog).Cast<object>());
                Assert.Null(LogicalTreeHelper.GetParent(foot));
            });
        }

        /// <summary>Every key of the dialogs' dictionary, as the dialog boards'
        /// parts are named. The record button is the pages' own, the face
        /// included, since the pages' record styles live in App.xaml.</summary>
        [Fact]
        public void TheDialogsDictionaryHoldsEveryKey()
        {
            DialogTestHost.Run(() =>
            {
                var looks = DialogWindow.Looks;
                Assert.IsType<Style>(looks[typeof(DialogWindow)]);
                Assert.IsType<ControlTemplate>(looks["DialogWindowTemplate"]);
                foreach (string key in new[]
                         {
                             "DialogFootPanel", "DialogButton", "DialogPrimaryButton", "DialogDangerButton", "DialogQuietButton",
                             "DialogSmallQuietButton", "DialogIconButton", "DialogClearButton", "DialogRecordButton", "DialogChipRemove",
                             "DialogLabel", "DialogLabelNarrow", "DialogHead3", "DialogNote", "DialogFigureNote", "DialogFootNote",
                             "DialogTextBox", "DialogPasswordBox", "DialogSelect", "DialogInputChipCombo", "DialogValueEdit", "DialogUnit",
                             "DialogList", "DialogListBox", "DialogListItem", "DialogListItemTall", "DialogListItemLinks",
                             "DialogPickListItem", "DialogRadioListItem", "DialogRadioListItemLinks", "DialogOptionItem", "DialogTag",
                             "DialogChip", "DialogSegmentRadio", "DialogLiveBox", "DialogWaitBox", "DialogCodeBox", "DialogStatTile",
                             "DialogStatTileWait", "DialogCountdownBar", "DialogMoreToggle", "DialogMoreRow",
                         })
                    Assert.True(looks.Contains(key), key + " is not in Dialogs.xaml");
            });
        }

        /// <summary>The frame moves by any part of it that does not take the
        /// click itself, and a border the pointer shows as a button is such a
        /// part: a drag would swallow the release its Click waits for.</summary>
        [Fact]
        public void APressOnSomethingClickable_DoesNotDragTheWindow()
        {
            DialogTestHost.Run(() =>
            {
                var chip = new PadForge.Controls.ClickBorder { Cursor = System.Windows.Input.Cursors.Hand };
                var word = new TextBlock();
                chip.Child = word;
                Assert.True(DialogWindow.PressLandedOnSomethingClickable(word));
                Assert.True(DialogWindow.PressLandedOnSomethingClickable(new Button()));
                Assert.True(DialogWindow.PressLandedOnSomethingClickable(new TextBox()));
                var plain = new Border { Child = new TextBlock() };
                Assert.False(DialogWindow.PressLandedOnSomethingClickable(plain.Child));
                Assert.False(DialogWindow.PressLandedOnSomethingClickable(null));
            });
        }

        /// <summary>The shared confirm as its callers make it: the action's
        /// word, or none for a notice, its kind and the word in Cancel's
        /// place.</summary>
        private static ConfirmDialog Confirm(string actionText, ConfirmKind kind = ConfirmKind.Destructive,
            string cancelText = null, string detail = null)
            => (ConfirmDialog)typeof(ConfirmDialog)
                .GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, null,
                    new[] { typeof(string), typeof(string), typeof(string), typeof(string), typeof(ConfirmKind), typeof(string) }, null)!
                .Invoke(new object[] { "Delete Rocket League?", "The profile is removed.", actionText, detail, kind, cancelText });

        /// <summary>A removal's action wears the danger look and a step
        /// forward the ember one. A caller's word replaces Cancel's. A notice
        /// has no action, and its one button reads Close or the caller's
        /// word. Cancel holds the default and the cancel in every form, so
        /// Enter and Esc land on it.</summary>
        [Fact]
        public void AConfirmTakesItsKind()
        {
            DialogTestHost.Run(() =>
            {
                var s = Strings.Instance;
                var looks = DialogWindow.Looks;

                var danger = Confirm("Delete");
                var action = (Button)danger.FindName("ActionButton");
                var cancel = (Button)danger.FindName("CancelButton");
                Assert.Equal("Delete Rocket League?", danger.Title);
                Assert.Equal("The profile is removed.", danger.Subtitle);
                Assert.Same(looks["DialogDangerButton"], action.Style);
                Assert.Equal("Delete", action.Content);
                Assert.Equal(Visibility.Visible, action.Visibility);
                Assert.Equal(s.Common_Cancel, cancel.Content);
                Assert.True(cancel.IsCancel && cancel.IsDefault);
                Assert.Equal(Visibility.Collapsed, ((TextBlock)danger.FindName("DetailText")).Visibility);

                var forward = Confirm("Apply", ConfirmKind.Primary, "Not Now", "3 axes, 12 buttons");
                action = (Button)forward.FindName("ActionButton");
                cancel = (Button)forward.FindName("CancelButton");
                Assert.Same(looks["DialogPrimaryButton"], action.Style);
                Assert.Equal("Not Now", cancel.Content);
                Assert.True(cancel.IsCancel && cancel.IsDefault);
                var detail = (TextBlock)forward.FindName("DetailText");
                Assert.Equal(Visibility.Visible, detail.Visibility);
                Assert.Equal("3 axes, 12 buttons", detail.Text);

                var notice = Confirm(null);
                Assert.Equal(Visibility.Collapsed, ((Button)notice.FindName("ActionButton")).Visibility);
                cancel = (Button)notice.FindName("CancelButton");
                Assert.Equal(s.Common_Close, cancel.Content);
                Assert.True(cancel.IsCancel && cancel.IsDefault);

                var worded = Confirm(null, cancelText: "OK");
                Assert.Equal(Visibility.Collapsed, ((Button)worded.FindName("ActionButton")).Visibility);
                Assert.Equal("OK", ((Button)worded.FindName("CancelButton")).Content);
            });
        }

        /// <summary>A sentence comes apart around its {0}, with its other
        /// places filled, so the {0} value can stand in a run of its own. A
        /// language may put {1} first.</summary>
        [Fact]
        public void FormatRunsSplitsAroundTheFirstPlace()
        {
            Assert.Equal(("Heard ", " on Mic"), FormatRuns.Split("Heard {0} on {1}", "Mic"));
            Assert.Equal(("Mic: ", "."), FormatRuns.Split("{1}: {0}.", "Mic"));
            Assert.Equal(("No place", string.Empty), FormatRuns.Split("No place", "x"));
            Assert.Equal((string.Empty, " at " + 0.5.ToString("F2", CultureInfo.CurrentCulture)), FormatRuns.Split("{0} at {1:F2}", 0.5));
            Assert.Equal(("Giving it back in ", " seconds"), FormatRuns.Split("Giving it back in {0} seconds"));
        }

        /// <summary>A Copy From row and its subtitle name a controller as
        /// its own page does: its type and its number among that type.</summary>
        [Fact]
        public void ACopyFromRowNamesItsSlot()
        {
            var pad = new PadForge.ViewModels.PadViewModel(3)
            {
                OutputType = PadForge.Engine.VirtualControllerType.Xbox,
                TypeInstanceLabel = "2",
            };
            Assert.Equal(Strings.Instance.ControllerType_Xbox + " #2", PadForge.MainWindow.CopyFromSlotName(pad));

            var row = new CopyFromDialog.DeviceEntry { Name = "Xbox #2", Detail = "DualSense Wireless Controller" };
            Assert.Equal("Xbox #2, DualSense Wireless Controller", row.ReadName);
            Assert.Equal("Xbox #2", new CopyFromDialog.DeviceEntry { Name = "Xbox #2" }.ReadName);
        }

        /// <summary>A sound's row carries its package and what its Play
        /// button is called, and the rows made for one package carry each
        /// sound's reference into it.</summary>
        [Fact]
        public void APickSoundRowCarriesItsPackage()
        {
            string value = PadForge.Common.SoundPackageManager.MakeRef("Pack", "a.wav");
            var item = new PickSoundDialog.Item("a.wav", "Pack", value);
            Assert.Equal("a.wav", item.ToString());
            Assert.Equal("Pack", item.Package);
            Assert.Equal(string.Format(Strings.Instance.PickSound_Play_Format, "a.wav"), item.PlayName);

            DialogTestHost.Run(() =>
            {
                var dialog = new PickSoundDialog("Pack", new[] { "a.wav", "b.wav" }, previewSlot: 0, previewVolume: 100);
                var list = (ListBox)dialog.FindName("SoundList");
                var rows = list.Items.Cast<PickSoundDialog.Item>().ToList();
                Assert.Equal(new[] { "a.wav", "b.wav" }, rows.Select(r => r.Name));
                Assert.All(rows, r => Assert.Equal("Pack", r.Package));
                Assert.Equal(new[] { value, PadForge.Common.SoundPackageManager.MakeRef("Pack", "b.wav") }, rows.Select(r => r.Value));
                // The first sound is picked, Browse Files is not offered, and
                // the dialog hands back the picked sound's reference.
                Assert.Equal(0, list.SelectedIndex);
                Assert.Equal(value, dialog.SelectedSound);
                Assert.Equal(Visibility.Collapsed, ((Button)dialog.FindName("BrowseButton")).Visibility);
            });
        }

        /// <summary>A removal row's first line is its kind in the app's
        /// language. Every kind the shipped HIDMaestro names has a string of
        /// its own: the Uninstaller's, read from the library's constants, so
        /// a kind a later HIDMaestro adds there fails here until it has one,
        /// and the usbip-win2 part's, which HIDMaestro writes as literals in
        /// UsbipRemoval.cs and lists in HMUninstallEntry.Kind's doc
        /// (HIDMaestro 2.4.0). A kind with no string shows in HIDMaestro's
        /// words with its first letter upper. The usbip-win2 part's own
        /// failure row names the product, which keeps its own
        /// spelling.</summary>
        [Fact]
        public void ARemovalRowsTitleIsItsKindInTheAppsLanguage()
        {
            var uninstaller = typeof(HIDMaestro.HMUninstallEntry).Assembly.GetType("HIDMaestro.Internal.Uninstall.Uninstaller", throwOnError: true);
            var constants = uninstaller.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                .Where(f => f.IsLiteral && f.FieldType == typeof(string) && f.Name.StartsWith("Kind", StringComparison.Ordinal))
                .Select(f => (string)f.GetRawConstantValue())
                .ToList();
            Assert.Equal(15, constants.Count);
            // UsbipRemoval.cs at v2.4.0: its devnode, driver package, registry
            // key and folder rows share the Uninstaller's kinds.
            var usbip = new[] { "devnode", "driver package", "service", "registry key", "registry value", "scheduled task",
                "folder", "file", "Apps entry", "hardware id", "USB/IP import", "upper filter" };
            var kinds = constants.Concat(usbip).Distinct().ToList();
            Assert.Equal(23, kinds.Count);
            // By key, not by English text: a kind with no key shows its own
            // words with the first letter upper, which for most kinds is the
            // English string too, and only a translation tells them apart.
            var keys = typeof(Strings).GetProperties()
                .Where(p => p.Name.StartsWith("HIDMaestroUninstall_Kind", StringComparison.Ordinal))
                .Select(p => p.Name)
                .ToList();
            var titleKeys = kinds.Select(UninstallHIDMaestroDialog.KindKey).ToList();
            Assert.All(titleKeys, key => Assert.Contains(key, keys));
            Assert.Equal(kinds.Count, titleKeys.Distinct().Count());
            Assert.Equal(keys.Count, titleKeys.Count);
            Assert.All(kinds, kind => Assert.Equal(Strings.Get(UninstallHIDMaestroDialog.KindKey(kind)), UninstallHIDMaestroDialog.KindTitle(kind)));
            Assert.Null(UninstallHIDMaestroDialog.KindKey("firmware slot"));
            Assert.Equal(Strings.Instance.HIDMaestroUninstall_KindDevice, UninstallHIDMaestroDialog.KindTitle("devnode"));
            Assert.Equal(Strings.Instance.HIDMaestroUninstall_KindPackage, UninstallHIDMaestroDialog.KindTitle("driver package"));
            Assert.Equal("Firmware slot", UninstallHIDMaestroDialog.KindTitle("firmware slot"));
            Assert.Equal("usbip-win2", UninstallHIDMaestroDialog.KindTitle("usbip-win2"));
            Assert.Equal(string.Empty, UninstallHIDMaestroDialog.KindTitle(string.Empty));
            Assert.Null(UninstallHIDMaestroDialog.KindTitle(null));
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }

        /// <summary>The markup of every dialog on the frame: each file of
        /// Views whose root is the frame.</summary>
        private static List<(string File, string Text, XDocument Doc)> FrameDialogs()
        {
            var found = new List<(string, string, XDocument)>();
            foreach (string path in Directory.GetFiles(Path.Combine(RepoRoot(), "PadForge.App", "Views"), "*.xaml"))
            {
                string text = File.ReadAllText(path);
                var doc = XDocument.Parse(text);
                if (doc.Root!.Name.LocalName == "DialogWindow") found.Add((Path.GetFileName(path), text, doc));
            }
            return found;
        }

        /// <summary>The frame merges the dialogs' dictionary into each
        /// dialog's own resources before the dialog's markup loads. A
        /// ResourceDictionary element under a dialog's Resources would
        /// replace that dictionary, and the dialog would open with none of
        /// the frame's looks.</summary>
        [Fact]
        public void EveryDialogListsItsResourcesDirectly()
        {
            var dialogs = FrameDialogs();
            Assert.True(dialogs.Count >= 2, "the walk found " + dialogs.Count + " dialogs on the frame");
            var replaced = dialogs
                .Where(d => d.Doc.Root!.Elements().Where(e => e.Name.LocalName == "DialogWindow.Resources")
                    .SelectMany(r => r.Elements()).Any(e => e.Name.LocalName == "ResourceDictionary"))
                .Select(d => d.File);
            Assert.Empty(replaced);
        }

        /// <summary>Each resource a dialog on the frame names by
        /// StaticResource is defined: in the dialog itself, in the dialogs'
        /// dictionary, or in the app's. One the app defines has a stand-in in
        /// the dialog tests' host, since a test host cannot load the app's
        /// dictionaries and a dialog with a missing key does not
        /// construct. The dialogs' dictionary is held to the same.</summary>
        [Fact]
        public void EveryStaticResourceADialogNames_IsDefinedAndStoodIn()
        {
            string root = RepoRoot();
            static HashSet<string> Keys(string text)
                => Regex.Matches(text, "x:Key=\"([A-Za-z0-9_]+)\"").Select(m => m.Groups[1].Value).ToHashSet();
            var looks = Keys(File.ReadAllText(Path.Combine(root, "PadForge.App", "Resources", "Dialogs.xaml")));
            var app = new HashSet<string>();
            foreach (string file in new[] { "App.xaml", Path.Combine("Resources", "ControllerIcons.xaml"), Path.Combine("Resources", "Shell.xaml") })
                app.UnionWith(Keys(File.ReadAllText(Path.Combine(root, "PadForge.App", file))));

            HashSet<string> stoodIn = null;
            DialogTestHost.Run(() =>
            {
                var stand = new ResourceDictionary();
                DialogTestHost.StandIns(stand);
                stoodIn = stand.Keys.OfType<string>().ToHashSet();
            });

            var dialogs = FrameDialogs();
            Assert.True(dialogs.Count >= 2, "the walk found " + dialogs.Count + " dialogs on the frame");
            var files = dialogs.Select(d => (d.File, d.Text)).ToList();
            files.Add(("Dialogs.xaml", File.ReadAllText(Path.Combine(root, "PadForge.App", "Resources", "Dialogs.xaml"))));
            var undefined = new List<string>();
            var notStoodIn = new List<string>();
            foreach (var (file, text) in files)
            {
                var own = Keys(text);
                foreach (string key in Regex.Matches(text, @"\{StaticResource\s+([A-Za-z0-9_]+)\}").Select(m => m.Groups[1].Value).Distinct())
                {
                    if (own.Contains(key) || looks.Contains(key)) continue;
                    if (!app.Contains(key)) undefined.Add(file + ": " + key);
                    else if (!stoodIn.Contains(key)) notStoodIn.Add(file + ": " + key);
                }
            }
            Assert.Empty(undefined);
            Assert.Empty(notStoodIn);
        }
    }
}
