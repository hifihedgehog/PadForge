using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Xml.Linq;
using PadForge.Common;
using PadForge.Search;
using PadForge.Services.Catalog;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The catalog (#600): Search's index grown to every dialog, every
    /// button's menu, the tray menu and the live objects, each entry with its
    /// element's screen-reader words, one registry for what is open, and
    /// stable IDs.
    /// </summary>
    public class CatalogTests
    {
        private static readonly Lazy<IReadOnlyList<SearchEntry>> Pages = new(() => SearchIndexBuilder.Build(SearchSources.Load()));
        private static readonly Lazy<IReadOnlyList<SearchEntry>> Dialogs = new(() => SearchIndexBuilder.BuildCatalog(SearchSources.LoadCatalog()));

        // ── Dialogs ──

        [Fact]
        public void EveryDialogsMarkup_IsCarried_AndEachDialogIsAnEntry()
        {
            var sources = SearchSources.LoadCatalog();
            var expected = Directory.EnumerateFiles(Path.Combine(ReachMarkup.AppRoot, "Views"), "*Dialog.xaml")
                .Select(Path.GetFileNameWithoutExtension)
                .Concat(new[] { "AutomationRequestCard", "AddControllerView", "SearchPanel", "RecordingReviewView" })
                .OrderBy(n => n, StringComparer.Ordinal).ToList();
            Assert.Equal(expected.Concat(new[] { "AboutPage", "HomePage", "MainWindow" }).OrderBy(n => n, StringComparer.Ordinal),
                sources.Keys.OrderBy(n => n, StringComparer.Ordinal));
            foreach (string name in expected)
                Assert.Single(Dialogs.Value, e => e.Kind == SearchKind.Dialog && e.Dialog == name);
            // A dialog's own buttons are its entries, its answers included.
            Assert.Contains(Dialogs.Value, e => e.Dialog == "ConfirmDialog" && e.Kind == SearchKind.Action);
            Assert.Contains(Dialogs.Value, e => e.Dialog == "ProfileDialog" && e.Kind == SearchKind.Dialog && e.Label.Key == "ProfileDialog_SaveAs");
        }

        /// <summary>Every dialog either opens from a page's button that a
        /// test can read in the code behind the page, or PadForge opens it
        /// itself. Never both, never neither.</summary>
        [Fact]
        public void EveryDialog_HasItsOpener_ReadInTheCodeBehindItsPage()
        {
            foreach (string name in SearchSources.LoadCatalog().Keys.Where(n => SearchIndexBuilder.CatalogPageOf(n) == SearchPage.Dialog))
                Assert.True(DialogCatalog.Openers.ContainsKey(name) ^ DialogCatalog.NoPageOpener.Contains(name), name);
            string app = string.Join("\n", Directory.EnumerateFiles(ReachMarkup.AppRoot, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)).Select(File.ReadAllText));
            foreach (var (dialog, (page, handler)) in DialogCatalog.Openers)
            {
                string markup = SearchSources.Load()[page];
                if (handler.EndsWith("Command", StringComparison.Ordinal))
                {
                    // A command of the page's data: its element names it, and
                    // the app makes the dialog.
                    Assert.Contains("Command=\"{Binding " + handler, markup);
                    Assert.Matches(@"new (?:\w+\.)*" + dialog + @"\b", app);
                    Assert.True(Pages.Value.Any(e => e.Kind is SearchKind.Action or SearchKind.MenuItem && e.Page == page && e.Handler == handler), handler);
                    continue;
                }
                Assert.Contains("Click=\"" + handler + "\"", markup);
                string code = File.ReadAllText(Path.Combine(ReachMarkup.AppRoot, "Views", SearchSources.FileNames[page] + ".cs"));
                var body = Regex.Match(code, @"void " + handler + @"\(object sender, RoutedEventArgs e\)\s*\{(?<body>.*?)\r?\n        \}", RegexOptions.Singleline);
                Assert.True(body.Success, handler);
                Assert.Matches(@"new " + dialog + @"\b|" + dialog + @"\.Show", body.Groups["body"].Value);
                Assert.True(Pages.Value.Any(e => e.Kind is SearchKind.Action or SearchKind.MenuItem && e.Page == page && e.Handler == handler), handler + " is no action of the index");
            }
        }

        [Fact]
        public void DialogIds_NameTheDialog_AndAreDistinct()
        {
            var ids = SearchIds.Assign(Dialogs.Value);
            Assert.Equal(ids.Count, ids.Values.Distinct().Count());
            var profile = Dialogs.Value.Single(e => e.Kind == SearchKind.Dialog && e.Dialog == "ProfileDialog");
            Assert.Equal("dialog.dialog/ProfileDialog/ProfileDialog_SaveAs", ids[profile]);
            Assert.All(Dialogs.Value.Where(e => e.Page == SearchPage.Dialog), e => Assert.StartsWith("dialog", ids[e]));
        }

        // ── Menus ──

        [Fact]
        public void EveryButtonsMenu_IsInTheIndex_UnderItsButton()
        {
            var items = Pages.Value.Where(e => e.Kind == SearchKind.MenuItem).ToList();
            Assert.Contains(items, e => e.Page == SearchPage.Pad && e.Label.Key == "Pad_CopyFrom");
            Assert.Contains(items, e => e.Page == SearchPage.Pad && e.Label.Key == "Main_DeleteVC");
            // Every MenuItem with words of its own in the five pages' markup
            // is one, a template's and the macro editor's included, and the
            // macro editor's ask (#600).
            int inMarkup = 0;
            foreach (var (page, text) in SearchSources.Load())
                inMarkup += XDocument.Parse(text).Descendants().Count(d => d.Name.LocalName == "MenuItem" && ReachMarkup.Attr(d, "Header")?.Contains("Strings.Instance") == true);
            Assert.Equal(inMarkup, items.Count);
            Assert.Contains(items, e => e.Label.Key == "Macro_ClearTrigger" && e.Reach == ReachClass.Asks && e.Why == ReachReason.None);
        }

        /// <summary>The macro menus' items ask as what each does (#600):
        /// Remove, Copy and Clear Trigger send no keys or mouse input, so
        /// their card gives no such reason, while Duplicate, Paste and Copy
        /// From make macros that do. An item's own reason stands at the
        /// section's class, as Reach.Of reads it.</summary>
        [Fact]
        public void TheMacroMenus_RemoveCopyAndClearTrigger_AskWithNoInputReason()
        {
            var macro = Pages.Value.Where(e => e.Kind == SearchKind.MenuItem && e.Page == SearchPage.Pad && e.Handler != null).ToList();
            foreach (string handler in new[] { "RemoveMacroCommand", "CopyMacroCommand", "SelectedMacro.ClearTriggerCommand" })
            {
                var items = macro.Where(e => e.Handler == handler).ToList();
                Assert.NotEmpty(items);
                Assert.All(items, e =>
                {
                    Assert.Equal((ReachClass.Asks, ReachReason.None), (e.Reach, e.Why));
                    Assert.Equal(PadForge.Resources.Strings.Strings.Instance.Automation_Ask_Always, PadForge.Services.Automation.AssistantToolWords.Reason(e.Why));
                });
            }
            Assert.Equal(2, macro.Count(e => e.Handler == "CopyMacroCommand"));
            foreach (string handler in new[] { "DuplicateMacroCommand", "PasteMacroCommand", "CopyMacroFromCommand" })
                Assert.All(macro.Where(e => e.Handler == handler), e => Assert.Equal((ReachClass.Asks, ReachReason.SendsInput), (e.Reach, e.Why)));
        }

        [Fact]
        public void TheTrayMenu_IsOneListForTheMenuAndTheCatalog()
        {
            var entries = TrayMenu.Entries();
            Assert.Equal(TrayMenu.Items.Select(i => i.StringKey), entries.Select(e => e.Label.Key));
            var ids = SearchIds.Assign(entries);
            Assert.Equal("window.menuitem/Tray/Main_ShowPadForge", ids[entries[0]]);
            Assert.All(entries, e => Assert.NotEqual(ReachClass.Unset, e.Reach));
            string code = File.ReadAllText(Path.Combine(ReachMarkup.AppRoot, "MainWindow.xaml.cs"));
            Assert.Contains("foreach (var item in PadForge.Services.Catalog.TrayMenu.Items)", code);
        }

        // ── One source for the words ──

        [Fact]
        public void EachEntry_CarriesItsElementsScreenReaderWords()
        {
            var level = Pages.Value.Single(e => e.Kind == SearchKind.Setting && e.Label.Key == "Extras_Automation_Level");
            Assert.Equal("Extras_Automation_Level", level.Name.Key);
            Assert.Equal("Automation_Level", level.AutomationId);
            var code = Pages.Value.Single(e => e.Kind == SearchKind.Action && e.Label.Key == "Dashboard_WebNewAccessCode");
            Assert.Equal("Dashboard_WebAccessCodeHint", code.Help.Key);
            Assert.Equal(ReachClass.UsersOwn, code.Reach);
            Assert.True(Pages.Value.Count(e => e.Help != null) > 250, "help read for " + Pages.Value.Count(e => e.Help != null));
        }

        /// <summary>Hover help and a screen reader read one source: WPF hands
        /// a screen reader the tooltip as the help text when HelpText is not
        /// set, so no element sets the two to different words.</summary>
        [Fact]
        public void NoElement_SaysOneThingOnHoverAndAnotherToAScreenReader()
        {
            var drift = new List<string>();
            foreach (string file in ReachMarkup.Files())
            {
                var doc = XDocument.Parse(File.ReadAllText(file), LoadOptions.SetLineInfo);
                foreach (var e in doc.Descendants())
                {
                    string help = ReachMarkup.Attr(e, "AutomationProperties.HelpText"), tip = ReachMarkup.Attr(e, "ToolTip");
                    // Two string keys that differ: two sources for one
                    // element's words. Words from the item's own data are
                    // one source.
                    if (help != null && tip != null && help != tip && help.Contains("Strings.Instance") && tip.Contains("Strings.Instance"))
                        drift.Add(Path.GetFileName(file) + ":" + ((System.Xml.IXmlLineInfo)e).LineNumber);
                }
            }
            Assert.Empty(drift);
        }

        // ── Live objects ──

        [Fact]
        public void APluginsShortId_IsStable_AndNamesNoPath()
        {
            string id = LiveCatalog.ShortId(@"C:\Users\Someone\Plugins\wheel.exe");
            Assert.Equal(id, LiveCatalog.ShortId(@"c:\users\someone\plugins\WHEEL.EXE"));
            Assert.Matches("^[0-9a-f]{8}$", id);
            Assert.Equal(new[] { "Device", "Controller", "Profile", "Macro", "Plugin", "PluginDeclaration", "Setting", "Action", "MenuItem", "Page" }, Enum.GetNames<LiveKind>());
        }

        /// <summary>The one seam for what a template draws or a program
        /// declares: settings with a value, actions, menus, the dialog a
        /// press opens, each with its class. An item with no class is
        /// left out.</summary>
        [Fact]
        public void ALiveObjectsSettingsActionsAndMenus_JoinThroughOneSeam()
        {
            string value = "40";
            Func<IReadOnlyList<LiveItem>> provider = () => new[]
            {
                new LiveItem("plugin/0a1b2c3d/speed", LiveKind.PluginDeclaration, "Speed", ReachClass.Free) { Read = () => value, Write = v => value = v },
                new LiveItem("row/3/menu", LiveKind.Action, "More", ReachClass.Free)
                {
                    Menu = new[] { new LiveItem("row/3/menu/test", LiveKind.MenuItem, "Release Test", ReachClass.Free) { Opens = "ReleaseTestDialog" } },
                },
                new LiveItem("db/row/1", LiveKind.Setting, "Row", ReachClass.Unset),
            };
            // Only this provider's items are looked at: tests that run beside
            // this one register providers of their own (a plugins' part's,
            // the database's).
            var mine = new HashSet<string> { "plugin/0a1b2c3d/speed", "row/3/menu", "row/3/menu/test", "db/row/1" };
            LiveCatalog.AddProvider(provider);
            try
            {
                var entries = LiveCatalog.Entries(null).Where(e => mine.Contains(e.Id)).ToList();
                Assert.Equal(new[] { "plugin/0a1b2c3d/speed", "row/3/menu", "row/3/menu/test" }, entries.Select(e => e.Id));
                var speed = (LiveItem)entries[0].Payload;
                speed.Write("55");
                Assert.Equal("55", speed.Read());
                Assert.Equal("ReleaseTestDialog", ((LiveItem)entries[2].Payload).Opens);
            }
            finally { LiveCatalog.RemoveProvider(provider); }
            Assert.DoesNotContain(LiveCatalog.Entries(null), e => mine.Contains(e.Id));
        }

        [Fact]
        public void HomeAboutAndTheWindowsOwnParts_AreInTheCatalog()
        {
            // Home draws its cards and their buttons in code (HomePage.Cards.cs):
            // its markup is read, and holds no action or setting of its own.
            Assert.Contains("HomePage", SearchSources.LoadCatalog().Keys);
            Assert.Contains(Dialogs.Value, e => e.Page == SearchPage.About);
            // The title bar's buttons, by their screen-reader names.
            Assert.Contains(Dialogs.Value, e => e.Page == SearchPage.Window && e.Kind == SearchKind.Action && e.Label.Key == "Main_FullScreen");
            Assert.DoesNotContain(Dialogs.Value, e => e.Kind == SearchKind.Dialog && e.Page != SearchPage.Dialog);
            Assert.All(Dialogs.Value.Where(e => e.Page != SearchPage.Dialog), e => Assert.Null(e.Dialog));
        }

        /// <summary>WPF hands a screen reader the tooltip as the help text
        /// when HelpText is not set: the one-source rule leans on it.</summary>
        [Fact]
        public void AScreenReader_HearsTheTooltip_WhenNoHelpTextIsSet()
            => Sta(() =>
            {
                var plain = new Button { ToolTip = "Turns it on." };
                Assert.Equal("Turns it on.", System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(plain).GetHelpText());
                var element = new Button { ToolTip = new ToolTip { Content = "Turns it on." } };
                string fromElement = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(element).GetHelpText();
                Assert.Equal("Turns it on.", fromElement);
                var both = new Button { ToolTip = "Hover words." };
                AutomationProperties.SetHelpText(both, "Reader words.");
                Assert.Equal("Reader words.", System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(both).GetHelpText());
            });

        [Fact]
        public void TheNewKindsAndPages_AreAppended()
        {
            Assert.Equal(new[] { "Setting", "Card", "Action", "Extra", "Section", "Part", "Dialog", "MenuItem" }, Enum.GetNames<SearchKind>());
            Assert.Equal(new[] { "Pad", "Settings", "Extras", "Devices", "Profiles", "Dialog", "Window", "Home", "About" }, Enum.GetNames<SearchPage>());
            Assert.Equal(new[] { "Pages", "Controllers", "Devices", "Profiles", "Settings", "Actions", "Docs", "Dialogs", "Macros", "Plugins" }, Enum.GetNames<SearchGroup>());
        }

        // ── What is open ──

        private static void Sta(Action action)
        {
            Exception failure = null;
            var thread = new Thread(() => { try { action(); } catch (Exception e) { failure = e; } }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(20000), "the run timed out");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }

        [Fact]
        public void TheRegistry_ReadsAnOpenDialogsTitleTextAndButtons_WithTheirClasses()
            => Sta(() =>
            {
                var panel = new StackPanel();
                panel.Children.Add(new TextBlock { Text = "Remove the profile?" });
                var cancel = new Button { Content = "Cancel" };
                AutomationProperties.SetAutomationId(cancel, "Dialog_Cancel");
                var remove = new Button { Content = "Remove", IsEnabled = false };
                Reach.SetClass(remove, ReachClass.UsersOwn);
                panel.Children.Add(cancel);
                panel.Children.Add(remove);
                var window = new Window { Title = "Profiles", Content = panel, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
                Reach.SetClass(window, ReachClass.Asks);
                window.Show();
                try
                {
                    DialogRegistry.Register(window);
                    Assert.Contains(window, DialogRegistry.Snapshot());
                    var open = DialogRegistry.Describe(window);
                    Assert.Equal("Profiles", open.Title);
                    Assert.Contains("Remove the profile?", open.Text);
                    Assert.Equal(new[] { "Cancel", "Remove" }, open.Buttons.Select(b => b.Words));
                    Assert.Equal("Dialog_Cancel", open.Buttons[0].AutomationId);
                    Assert.Equal(ReachClass.Asks, open.Buttons[0].Reach);
                    Assert.Equal(ReachClass.UsersOwn, open.Buttons[1].Reach);
                    Assert.False(open.Buttons[1].Enabled);
                    Assert.Equal(ReachClass.Asks, open.Reach);
                }
                finally { window.Close(); }
                Assert.DoesNotContain(window, DialogRegistry.Snapshot());
            });

        [Fact]
        public void AShellDialog_IsInTheRegistryWhileItIsOpen()
        {
            string code = File.ReadAllText(Path.Combine(ReachMarkup.AppRoot, "Controls", "ShellDialog.cs"));
            Assert.Contains("PadForge.Services.Catalog.DialogRegistry.Register(dialog);", code);
            Assert.Contains("PadForge.Services.Catalog.DialogRegistry.Unregister(dialog);", code);
            string app = File.ReadAllText(Path.Combine(ReachMarkup.AppRoot, "App.xaml.cs"));
            Assert.Contains("PadForge.Services.Catalog.DialogRegistry.Watch();", app);
        }
    }
}
