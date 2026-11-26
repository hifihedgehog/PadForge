using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using PadForge.Common;
using PadForge.Search;
using PadForge.Services.Automation;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Reads the class every element of a markup file ends up with (#600),
    /// the way <see cref="Reach.Of"/> climbs: the nearest Reach.Class on the
    /// element or above it. A keyed resource stands alone, since WPF draws
    /// it wherever it is used: it takes its own class, or the file's when
    /// the file has one class only. A style's or a control template's
    /// parts are the look of a control and carry none.
    /// </summary>
    internal static class ReachMarkup
    {
        internal static readonly HashSet<string> Actions = new(StringComparer.Ordinal)
            { "Button", "ToggleButton", "RadioButton", "MenuItem", "SettingResetButton", "Hyperlink", "RepeatButton", "SplitButton", "DropDownButton" };

        internal static readonly HashSet<string> Settable = new(StringComparer.Ordinal)
            { "CheckBox", "ComboBox", "Slider", "TextBox", "NumberBox", "PasswordBox", "RangeSlider", "CurveEditor", "ListBox", "ToggleSwitch", "AutoSuggestBox" };

        private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        internal static string Attr(XElement e, string local)
            => e.Attributes().FirstOrDefault(a => a.Name.LocalName == local && a.Name.Namespace != Xaml)?.Value;

        /// <summary>Whether an element is an action, a settable element or
        /// bound to data (any binding but a string's).</summary>
        internal static bool Counts(XElement e)
        {
            string local = e.Name.LocalName;
            if (local.Contains('.') || local is "DataTrigger" or "Trigger" or "MultiDataTrigger" or "Condition" or "Setter" or "EventTrigger") return false;
            if (Actions.Contains(local) || Settable.Contains(local)) return true;
            return e.Attributes().Any(a => a.Value.StartsWith("{Binding", StringComparison.Ordinal) && !a.Value.Contains("Strings.Instance", StringComparison.Ordinal)
                                           && a.Name.LocalName is not ("Visibility" or "Style" or "Foreground" or "Background" or "Opacity" or "IsEnabled"));
        }

        /// <summary>Each element that counts, with the class it ends up
        /// with, its line and whether it sits in a keyed resource.</summary>
        internal static List<(XElement Element, ReachClass Class, int Line, bool Keyed)> Read(string text)
        {
            var doc = XDocument.Parse(text, LoadOptions.SetLineInfo);
            var found = new List<(XElement, ReachClass, int, bool)>();
            var fileClasses = doc.Descendants().Select(d => Attr(d, "Reach.Class")).Where(v => v != null).Distinct().ToList();
            ReachClass rootClass = Parse(Attr(doc.Root, "Reach.Class"));
            void Walk(XElement e, ReachClass inherited, bool look, bool keyed)
            {
                string local = e.Name.LocalName;
                if (local is "Style" or "ControlTemplate" || local.EndsWith(".Template", StringComparison.Ordinal) || local.EndsWith(".Style", StringComparison.Ordinal)) look = true;
                bool isResource = e.Parent != null && e.Parent.Name.LocalName.EndsWith(".Resources", StringComparison.Ordinal) && e.Attribute(Xaml + "Key") != null;
                if (isResource)
                {
                    keyed = true;
                    inherited = fileClasses.Count <= 1 ? rootClass : ReachClass.Unset;
                }
                // The strictest on the way down, as Reach.Of reads it.
                var own = Parse(Attr(e, "Reach.Class"));
                if (own > inherited) inherited = own;
                if (!look && Counts(e)) found.Add((e, inherited, ((System.Xml.IXmlLineInfo)e).LineNumber, keyed));
                foreach (var c in e.Elements()) Walk(c, inherited, look, keyed);
            }
            Walk(doc.Root, ReachClass.Unset, false, false);
            return found;
        }

        internal static ReachClass Parse(string value) => Enum.TryParse(value, out ReachClass c) ? c : ReachClass.Unset;

        internal static string AppRoot => Path.Combine(OffLimitsSource.RepoRoot(), "PadForge.App");

        internal static IEnumerable<string> Files()
            => Directory.EnumerateFiles(AppRoot, "*.xaml", SearchOption.AllDirectories)
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                            && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                            && !f.Contains("publish_output"))
                .OrderBy(f => f, StringComparer.Ordinal);
    }

    /// <summary>Reach classes (#600): every action, dialog button, setting
    /// and data-bound element has one, the user's own are pinned, and a read
    /// is scrubbed by them.</summary>
    public class ReachClassTests
    {
        /// <summary>Controls drawn inside a page or dialog, which take the
        /// class of the place that holds them.</summary>
        private static bool IsHosted(string file, string root, IReadOnlyList<string> allTexts)
        {
            if (root is not ("UserControl" or "Grid" or "Border")) return false;
            string name = Path.GetFileNameWithoutExtension(file);
            return allTexts.Any(t => t.Contains(":" + name + " ", StringComparison.Ordinal) || t.Contains(":" + name + "/>", StringComparison.Ordinal)
                                     || t.Contains(":" + name + "\r", StringComparison.Ordinal) || t.Contains(":" + name + "\n", StringComparison.Ordinal)
                                     || t.Contains(":" + name + ">", StringComparison.Ordinal));
        }

        [Fact]
        public void EveryActionSettingAndBoundElement_HasAClass()
        {
            var files = ReachMarkup.Files().ToList();
            var texts = files.Select(File.ReadAllText).ToList();
            var missing = new List<string>();
            int counted = 0;
            for (int i = 0; i < files.Count; i++)
            {
                var doc = XDocument.Parse(texts[i]);
                string root = doc.Root.Name.LocalName;
                if (root is "ResourceDictionary" or "Application") continue;
                bool hosted = ReachMarkup.Attr(doc.Root, "Reach.Class") == null && IsHosted(files[i], root, texts.Where((_, j) => j != i).ToList());
                foreach (var (element, cls, line, _) in ReachMarkup.Read(texts[i]))
                {
                    counted++;
                    if (cls == ReachClass.Unset && !hosted)
                        missing.Add(Path.GetRelativePath(ReachMarkup.AppRoot, files[i]) + ":" + line + " <" + element.Name.LocalName + ">");
                }
            }
            Assert.True(counted > 2000, "the walk read " + counted + " elements");
            Assert.True(missing.Count == 0, "No reach class:\n" + string.Join("\n", missing.Take(60)));
        }

        /// <summary>The check finds a planted button with no class: the
        /// walk is no empty pass.</summary>
        [Fact]
        public void APlantedUnclassedButton_IsFound()
        {
            const string planted = "<UserControl xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" xmlns:common=\"clr-namespace:PadForge.Common\">"
                                   + "<StackPanel><Button Content=\"Go\"/><StackPanel common:Reach.Class=\"Asks\"><CheckBox/></StackPanel></StackPanel></UserControl>";
            var read = ReachMarkup.Read(planted);
            Assert.Equal(ReachClass.Unset, read.Single(r => r.Element.Name.LocalName == "Button").Class);
            Assert.Equal(ReachClass.Asks, read.Single(r => r.Element.Name.LocalName == "CheckBox").Class);
        }

        /// <summary>The Yours Alone list, pinned: every place the markup
        /// makes the user's alone. The parts later items add (the web switch,
        /// the install buttons, the plugin approvals of the Plugins card)
        /// join this list when they are built.</summary>
        [Fact]
        public void TheYoursAloneList_IsPinned()
        {
            var expected = new[]
            {
                "Views/AnswerPairDialog.xaml <DialogWindow>",             // a pairing's QR code and six digits (#600)
                "Views/AssistantCallout.xaml <UserControl>",              // the guided pointer's callout and its Close (#600)
                "Views/AssistantsAnswerRow.xaml <UserControl>",           // Answer From Another Device: Pair a Device…, each device's Remove (#600)
                "Views/AssistantsWebCard.xaml <UserControl>",             // Assistants on the Web: its tunnel, Accept, Revoke (#600 item 11)
                "Views/AssistantsWebSwitch.xaml <UserControl>",           // Reach PadForge From the Web, the web switch (#600 item 11)
                "Views/AutomationRequestCard.xaml <Window>",              // the request card
                "Views/ExtrasPage.xaml <StackPanel AutomationPage>",      // Automation's own controls, approvals, installs, Activity
                // The Plugins page's own controls (#600 item 18): its switch, the
                // built-ins' switches, Replay, each plugin card's Start,
                // Approve and Restart, Remove and switch, Add a Plugin and More.
                // A plugin's declared settings are the plugin's, free to a
                // program at Change Settings (item 17).
                "Views/ExtrasPage.xaml <HeadRowPanel PluginsHead>",
                "Views/ExtrasPage.xaml <StackPanel PluginsTestControllerSwitch>",
                "Views/ExtrasPage.xaml <FoldList PluginsReplay>",
                "Views/ExtrasPage.xaml <StackPanel PluginRowActions>",
                "Views/ExtrasPage.xaml <Button {Binding RemoveId}>",
                // Show in Folder, and Trust New Versions From This Signer,
                // which lets a new file start without the question (#600 N9).
                "Views/ExtrasPage.xaml <Button {Binding ShowInFolderId}>",
                "Views/ExtrasPage.xaml <SettingRow PluginTrustRow>",
                "Views/ExtrasPage.xaml <Border PluginsAddCard>",
                "Views/ExtrasPage.xaml <FoldList PluginsMoreList>",
                "Views/ExtrasPage.xaml <Button NewWebAccessCodeButton>",  // an access code
                "Views/ExtrasPage.xaml <TextBox WebControllerAccessCodeBox>",
                "Views/RecordingReviewView.xaml <UserControl>",           // the review of a recording
                "Views/NetworkAssistantDialog.xaml <DialogWindow>",        // a network key, made and shown once (#600 item 53)
                "Views/NetworkPortDialog.xaml <DialogWindow>",             // the network door's port
                "Views/PickDevicesDialog.xaml <DialogWindow>",             // the devices a program's reach takes (#600 DESIGN-2 N3)
                "Views/RecordingWaitingCard.xaml <UserControl>",          // a waiting recording, what was typed among it
                "Views/RemoteLinkPairDialog.xaml <DialogWindow>",         // a pairing code
                "Views/RemoteLinkPasswordDialog.xaml <DialogWindow>",     // an access code
                "Views/SettingsPage.xaml <TextBlock SettingsFilePath>",   // the settings file's path
                "MainWindow.xaml <Button ConnectedMark>",                 // the title bar's mark names other programs (#600 item 13)
                "Views/SettingsPage.xaml <SettingItem WhatToRecord>",   // what a recording holds, Activity among it
            };
            var found = new List<string>();
            foreach (string file in ReachMarkup.Files())
            {
                var doc = XDocument.Parse(File.ReadAllText(file));
                foreach (var e in doc.Descendants())
                {
                    if (ReachMarkup.Attr(e, "Reach.Class") != "UsersOwn") continue;
                    string name = (string)e.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"))
                                  ?? ReachMarkup.Attr(e, "AutomationProperties.AutomationId")
                                  ?? (ReachMarkup.Attr(e, "Text") is { } t && t.Contains("SettingsFilePath") ? "SettingsFilePath" : null);
                    found.Add(Path.GetRelativePath(ReachMarkup.AppRoot, file).Replace('\\', '/') + " <" + e.Name.LocalName + (name != null ? " " + name : string.Empty) + ">");
                }
            }
            Assert.Equal(expected.OrderBy(x => x, StringComparer.Ordinal), found.OrderBy(x => x, StringComparer.Ordinal));
        }

        /// <summary>What asks, by its reason, pinned by place.</summary>
        [Fact]
        public void WhatAsks_IsPinned()
        {
            var found = new List<string>();
            foreach (string file in ReachMarkup.Files())
            {
                var doc = XDocument.Parse(File.ReadAllText(file));
                foreach (var e in doc.Descendants())
                    if (ReachMarkup.Attr(e, "Reach.Class") == "Asks")
                        found.Add(Path.GetFileName(file) + " " + (ReachMarkup.Attr(e, "Reach.Why") ?? "-"));
            }
            // The macro section asks for each of its keyed templates too,
            // since each sets its own class (the markup rule for a keyed
            // template in a file of more than one class). The ask-first sweep
            // (AskFirstSweepTests) adds what runs a program or opens a place:
            // a Workshop item's page, the device mapping issue, Move PadForge
            // to Its Own Folder on three pages, Restore the Windows Driver
            // twice, the Run Program step, a controller's Paste twice, Release
            // Notes, Install and Restart, both automatic update switches, Start
            // at Login and the Window card's Reset All, the Visual C++ 2008
            // runtime, Force Dimension's page, the Falcon's driver and Reset to
            // Defaults, which purges HidHide's cloaks. The extras that listen
            // on every address ask: four pages whole, and Head Tracking's
            // switch, its UDP input's and Game Telemetry's. So do Switch
            // Profiles by Game and the profile dialog's Save, which let the
            // foreground watcher put a profile that turns one on in force. A
            // layer tab's Paste and Clear ask as the controller's Paste and
            // Clear All's confirm do.
            Assert.Equal(new[]
            {
                "AboutPage.xaml OpensPlace x1", "CommunityConfigsPanel.xaml OpensPlace x1", "ConfirmDialog.xaml - x1",
                "DevicesPage.xaml Drivers x1", "DevicesPage.xaml OpensPlace x2", "DevicesPage.xaml RunsProgram x1", "DevicesPage.xaml UseOnly x4",
                "ExtrasPage.xaml Network x7", "GetProfilesDialog.xaml ImportsProfile x1", "HomePage.xaml RunsProgram x1", "ImportConfigDialog.xaml ImportsProfile x1",
                "KeepDriverDialog.xaml Drivers x1", "PadPage.xaml - x2", "PadPage.xaml ImportsProfile x2", "PadPage.xaml RunsProgram x1", "PadPage.xaml SendsInput x54",
                "ProfileDialog.xaml Network x1", "ProfilesPage.xaml ImportsProfile x1", "ProfilesPage.xaml Network x1",
                "SettingsPage.xaml - x1", "SettingsPage.xaml Drivers x3", "SettingsPage.xaml OpensPlace x4", "SettingsPage.xaml RunsProgram x8",
                "UninstallBthPS3Dialog.xaml Drivers x1", "UninstallHIDMaestroDialog.xaml Drivers x1",
            }, found.GroupBy(x => x).Select(g => g.Key + " x" + g.Count()).OrderBy(x => x, StringComparer.Ordinal));
        }

        /// <summary>The append-only numbers of the two enums.</summary>
        [Fact]
        public void TheClassesAndReasons_KeepTheirNumbers()
        {
            Assert.Equal(new[] { "Unset", "Free", "Asks", "UsersOwn" }, Enum.GetNames<ReachClass>());
            Assert.Equal(new[] { "None", "RunsProgram", "OpensPlace", "SendsInput", "ImportsProfile", "Network", "Drivers", "UseOnly" }, Enum.GetNames<ReachReason>());
        }

        /// <summary>Every entry of the index carries the class of its
        /// element, and what asked or was the user's own before the classes
        /// still is (#597's rules).</summary>
        [Fact]
        public void EveryEntry_HasAClass_AndNothingThatAskedBeforeIsFreeNow()
        {
            var pages = SearchIndexBuilder.Build(SearchSources.Load());
            var dialogs = SearchIndexBuilder.BuildCatalog(SearchSources.LoadCatalog());
            Assert.All(pages.Concat(dialogs), e => Assert.NotEqual(ReachClass.Unset, e.Reach));
            foreach (var e in pages.Where(e => e.Kind == SearchKind.Setting))
            {
                string path = e.Value?.Path ?? string.Empty, key = e.Label?.Key ?? string.Empty;
                bool askedBefore = e.ExtraKey is "WebController" or "RemoteLink" or "MotionServer" or "GameMods"
                                   || path.Contains("HidHide", StringComparison.OrdinalIgnoreCase) || key.Contains("HidHide", StringComparison.OrdinalIgnoreCase)
                                   || path.Contains("OnlyInPadForge", StringComparison.OrdinalIgnoreCase) || path.Contains("UseOnly", StringComparison.OrdinalIgnoreCase)
                                   || (e.Page == SearchPage.Settings && e.ScopeName is "DriversGroup");
                bool ownBefore = e.ExtraKey is "Automation" or "Plugins";
                if (ownBefore) Assert.Equal(ReachClass.UsersOwn, e.Reach);
                else if (askedBefore) Assert.True(e.Reach != ReachClass.Free, key + path + " asked before and is free now");
            }
            // The network extras ask, for turning them on.
            Assert.Contains(pages, e => e.ExtraKey == "WebController" && e.Reach == ReachClass.Asks && e.Why == ReachReason.Network);
        }

        [Fact]
        public void AReadOfTheUsersOwn_CarriesNoValue()
        {
            Assert.Null(AutomationReadScrub.ForReach(ReachClass.UsersOwn, "1234-5678"));
            Assert.Null(AutomationReadScrub.ForReach(ReachClass.Unset, "1234-5678"));
            Assert.Equal("yours-alone", AutomationReadScrub.ReachWord(ReachClass.UsersOwn));
            Assert.Equal("asks", AutomationReadScrub.ReachWord(ReachClass.Asks));
            Assert.Equal("free", AutomationReadScrub.ReachWord(ReachClass.Free));
        }

        private static void Sta(Action action)
        {
            Exception failure = null;
            var thread = new Thread(() => { try { action(); } catch (Exception e) { failure = e; } }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(20000), "the run timed out");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }

        /// <summary>At run time the class climbs the tree, takes the nearest,
        /// and climbs from a context menu to its button.</summary>
        [Fact]
        public void AtRunTime_TheNearestClassCounts_AndAMenuTakesItsButtons()
            => Sta(() =>
            {
                var page = new StackPanel();
                Reach.SetClass(page, ReachClass.Free);
                var card = new StackPanel();
                Reach.SetClass(card, ReachClass.Asks);
                Reach.SetWhy(card, ReachReason.Drivers);
                var inCard = new Button();
                var item = new MenuItem();
                inCard.ContextMenu = new ContextMenu { PlacementTarget = inCard };
                inCard.ContextMenu.Items.Add(item);
                card.Children.Add(inCard);
                var free = new Button();
                page.Children.Add(card);
                page.Children.Add(free);
                Assert.Equal((ReachClass.Asks, ReachReason.Drivers), Reach.Of(inCard));
                Assert.Equal((ReachClass.Asks, ReachReason.Drivers), Reach.Of(item));
                Assert.Equal((ReachClass.Free, ReachReason.None), Reach.Of(free));
                Assert.Equal(ReachClass.UsersOwn, Reach.Effective(new Button()));
            });
    }
}
