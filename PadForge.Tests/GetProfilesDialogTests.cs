using System;
using System.Linq;
using System.Xml.Linq;
using PadForge.Engine;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Get Profiles (#574): one window on the shared dialog frame with three
    /// tabs, each at its board's size, its buttons in the foot, and the
    /// community configs' Becomes rule.
    /// </summary>
    public class GetProfilesDialogTests
    {
        private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

        private static XDocument Dialog() => XDocument.Parse(ExtrasPageTests.RepoText("PadForge.App", "Views", "GetProfilesDialog.xaml"));

        private static XElement ById(XContainer scope, string id)
            => Assert.Single(scope.Descendants(), e => (string)e.Attribute("AutomationProperties.AutomationId") == id);

        [Fact]
        public void TheWindow_IsAWideDialogOnTheFrame()
        {
            var root = Dialog().Root;
            Assert.Equal("DialogWindow", root.Name.LocalName);
            Assert.Equal("True", (string)root.Attribute("AutomationProperties.IsDialog"));
            Assert.Equal("True", (string)root.Attribute("IsWide"));
            Assert.Equal("True", (string)root.Attribute("ShowClose"));
            Assert.Equal("{Binding GetProfiles_Title, Source={x:Static strings:Strings.Instance}}", (string)root.Attribute("Title"));
            Assert.Equal("{Binding GetProfiles_Intro, Source={x:Static strings:Strings.Instance}}", (string)root.Attribute("Subtitle"));
            // The dialog lists its resources directly, so the frame's merged
            // dictionary stays.
            var resources = Assert.Single(root.Elements(), e => e.Name.LocalName == "DialogWindow.Resources");
            Assert.DoesNotContain(resources.Elements(), e => e.Name.LocalName == "ResourceDictionary");
            // The frame's drag skips a press on anything clickable.
            string frame = ExtrasPageTests.RepoText("PadForge.App", "Views", "DialogWindow.cs");
            Assert.Contains("if (PressLandedOnSomethingClickable(e.OriginalSource as DependencyObject)) return;", frame, StringComparison.Ordinal);
            Assert.Contains("DragMove();", frame, StringComparison.Ordinal);
        }

        [Fact]
        public void TheTabs_AreStartersCommunityAndImportAFile()
        {
            var doc = Dialog();
            var track = Assert.Single(doc.Descendants(), e => (string)e.Attribute("Style") == "{StaticResource SegmentTrack}");
            var tabs = track.Descendants().Where(e => e.Name.LocalName == "RadioButton").ToList();
            Assert.Equal(new[] { "GetProfiles_Starters", "GetProfiles_Community", "GetProfiles_File" },
                tabs.Select(t => (string)t.Attribute("AutomationProperties.AutomationId")));
            Assert.Equal(new[] { "Starters", "Community", "File" }, tabs.Select(t => (string)t.Attribute("Tag")));
            Assert.All(tabs, t =>
            {
                Assert.Equal("{StaticResource DialogSegmentRadio}", (string)t.Attribute("Style"));
                Assert.Equal("Tab_Checked", (string)t.Attribute("Checked"));
                Assert.Null(t.Attribute("IsChecked"));
            });
            Assert.Equal(new[] { GetProfilesTab.Starters, GetProfilesTab.Community, GetProfilesTab.File },
                tabs.Select(t => Enum.Parse<GetProfilesTab>((string)t.Attribute("Tag"))));
        }

        [Fact]
        public void TheFoot_HoldsTheHintThenCancelSaveAndApplySaveAndImport()
        {
            var foot = Assert.Single(Dialog().Descendants(), e => e.Name.LocalName == "DialogWindow.Foot");
            var panel = Assert.Single(foot.Elements());
            Assert.Equal("{StaticResource DialogFootPanel}", (string)panel.Attribute("Style"));
            var parts = panel.Elements().ToList();
            Assert.Equal("{Binding GetProfiles_SaveHint, Source={x:Static strings:Strings.Instance}}", (string)parts[0].Attribute("Text"));
            Assert.Equal(new[] { "GetProfiles_Cancel", "GetProfiles_SaveApply", "GetProfiles_Save", "GetProfiles_Import" },
                parts.Skip(1).Select(p => (string)p.Attribute("AutomationProperties.AutomationId")));
            var cancel = ById(panel, "GetProfiles_Cancel");
            Assert.Equal("True", (string)cancel.Attribute("IsCancel"));
            Assert.Equal("{StaticResource DialogButton}", (string)cancel.Attribute("Style"));
            Assert.Equal("{StaticResource DialogPrimaryButton}", (string)ById(panel, "GetProfiles_Save").Attribute("Style"));
            Assert.Equal("{StaticResource DialogPrimaryButton}", (string)ById(panel, "GetProfiles_Import").Attribute("Style"));
        }

        [Fact]
        public void TheBodies_AreTheStartersTheCommunityPanelAndTheDropZone()
        {
            var doc = Dialog();
            var starters = Assert.Single(doc.Descendants(), e => (string)e.Attribute(X + "Name") == "StarterList");
            Assert.Equal("{StaticResource StarterItem}", (string)starters.Attribute("ItemContainerStyle"));
            var item = Assert.Single(doc.Descendants(), e => (string)e.Attribute(X + "Key") == "StarterItem");
            Assert.Equal("{StaticResource DialogOptionItem}", (string)item.Attribute("BasedOn"));
            Assert.Equal("StarterList_MouseDoubleClick", (string)starters.Attribute("MouseDoubleClick"));
            Assert.Equal("StarterList_KeyDown", (string)starters.Attribute("KeyDown"));
            Assert.Single(doc.Descendants(), e => e.Name.LocalName == "CommunityConfigsPanel" && (string)e.Attribute(X + "Name") == "CommunityPanel");
            var drop = ById(doc, "GetProfiles_FileDrop");
            Assert.Equal("True", (string)drop.Attribute("AllowDrop"));
            Assert.Equal("FileDropZone_Drop", (string)drop.Attribute("Drop"));
            Assert.Equal("FileDropZone_Click", (string)drop.Attribute("Click"));
        }

        [Fact]
        public void EachTab_HasItsBoardsSize()
        {
            Assert.Equal((680d, 568d, 560d, 480d), GetProfilesDialog.SizeFor(GetProfilesTab.Starters));
            Assert.Equal((1100d, 780d, 900d, 640d), GetProfilesDialog.SizeFor(GetProfilesTab.Community));
            Assert.Equal((680d, 568d, 560d, 480d), GetProfilesDialog.SizeFor(GetProfilesTab.File));
        }

        [Fact]
        public void Becomes_OffersXboxOrPlayStation_ForAGamepadHalf_AndKeyboardMouseAlone()
        {
            Assert.Equal((true, true, false, VirtualControllerType.Xbox),
                CommunityConfigsPanel.BecomesState(true, VirtualControllerType.Xbox));
            Assert.Equal((true, true, false, VirtualControllerType.PlayStation),
                CommunityConfigsPanel.BecomesState(true, VirtualControllerType.PlayStation));
            // Anything else the panel held reads as Xbox while there is a gamepad half.
            Assert.Equal((true, true, false, VirtualControllerType.Xbox),
                CommunityConfigsPanel.BecomesState(true, VirtualControllerType.KeyboardMouse));
            Assert.Equal((false, false, true, VirtualControllerType.KeyboardMouse),
                CommunityConfigsPanel.BecomesState(false, VirtualControllerType.Xbox));
            Assert.Equal((false, false, true, VirtualControllerType.KeyboardMouse),
                CommunityConfigsPanel.BecomesState(false, VirtualControllerType.PlayStation));
        }
    }

    /// <summary>
    /// The community configs of Get Profiles (#574): the rows' second lines,
    /// and what the Workshop browser drew that the flat board does not.
    /// </summary>
    public class CommunityConfigsPanelTests
    {
        [Fact]
        public void AConfigsMetaLine_JoinsWhoWhenVotesAndSubscribers()
        {
            var bare = new WorkshopConfigItem { HasVotes = false, HasSubs = false };
            bare.ByLine = "by Ann, 2 days ago";
            Assert.Equal("by Ann, 2 days ago", bare.MetaLine);

            var full = new WorkshopConfigItem
            {
                HasVotes = true, VotesText = "7 votes", VotePercentText = "86%",
                HasSubs = true, SubsText = "1.2k subscribers",
            };
            var raised = new System.Collections.Generic.List<string>();
            full.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            full.ByLine = "by Ann, 2 days ago";
            Assert.Equal("by Ann, 2 days ago · 7 votes · 86% · 1.2k subscribers", full.MetaLine);
            Assert.Contains(nameof(WorkshopConfigItem.MetaLine), raised);
        }

        [Fact]
        public void AGamesSubLine_JoinsItsCountAndItsControllerSupport()
        {
            var game = new WorkshopGameItem { ControllerSupport = "full" };
            Assert.Equal(PadForge.Resources.Strings.Strings.Instance.Workshop_ControllerSupportFull, game.SubLine);
            var raised = new System.Collections.Generic.List<string>();
            game.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            game.CountText = "12 configs";
            Assert.Equal("12 configs · " + PadForge.Resources.Strings.Strings.Instance.Workshop_ControllerSupportFull, game.SubLine);
            Assert.Contains(nameof(WorkshopGameItem.SubLine), raised);
            Assert.Equal("3 configs", new WorkshopGameItem { CountText = "3 configs" }.SubLine);
            Assert.Equal("3 configs · " + PadForge.Resources.Strings.Strings.Instance.Workshop_ControllerSupportPartial,
                new WorkshopGameItem { ControllerSupport = "partial", CountText = "3 configs" }.SubLine);
        }

        [Fact]
        public void TheHeroArtAvatarsAndOldFooter_AreGone()
        {
            string code = ExtrasPageTests.RepoText("PadForge.App", "Views", "CommunityConfigsPanel.xaml.cs");
            string xaml = ExtrasPageTests.RepoText("PadForge.App", "Views", "CommunityConfigsPanel.xaml");
            foreach (string gone in new[] { "GetAvatarCachedAsync", "ArtLayer", "HeroImage", "AvatarHttp", "ManifestFooter", "VoteBarWidth", "DotGlow" })
            {
                Assert.DoesNotContain(gone, code, StringComparison.Ordinal);
                Assert.DoesNotContain(gone, xaml, StringComparison.Ordinal);
            }
            Assert.StartsWith("<UserControl x:Class=\"PadForge.Views.CommunityConfigsPanel\"", xaml.TrimStart('\uFEFF'), StringComparison.Ordinal);
        }
    }
}
