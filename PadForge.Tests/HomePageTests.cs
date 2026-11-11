using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Serialization;
using PadForge.Common.Input;
using PadForge.Common.Input.Telemetry;
using PadForge.Common.Input.Twitch;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;
using Strings = PadForge.Resources.Strings.Strings;

namespace PadForge.Tests
{
    /// <summary>
    /// Home (#574), the page that took the Dashboard's place, and the shell
    /// around it: the sidebar's order and its engine foot, the profile pill
    /// in the title bar, and the message line where the status bar was.
    /// These hold the page to its decided layout, hold every AutomationId a
    /// UI tool finds a control by, hold Home's list of extras to the Extras
    /// page's ten, and pin the rule of Home's offer.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class HomePageTests : IDisposable
    {
        private readonly SettingsCollection _savedSettings = SettingsManager.UserSettings;
        private readonly DeviceCollection _savedDevices = SettingsManager.UserDevices;

        public void Dispose()
        {
            SettingsManager.UserSettings = _savedSettings;
            SettingsManager.UserDevices = _savedDevices;
        }

        private static string Home() => ExtrasPageTests.RepoText("PadForge.App", "Views", "HomePage.xaml");

        private static string Window() => ExtrasPageTests.RepoText("PadForge.App", "MainWindow.xaml");

        private static string WindowCode() => ExtrasPageTests.RepoText("PadForge.App", "MainWindow.xaml.cs");

        private static int Count(string text, string needle) =>
            text.Split(new[] { needle }, StringSplitOptions.None).Length - 1;

        /// <summary>Where a needle that must be in the text once sits.</summary>
        private static int At(string text, string needle)
        {
            int at = text.IndexOf(needle, StringComparison.Ordinal);
            Assert.True(at >= 0, needle + " is not there");
            Assert.True(at == text.LastIndexOf(needle, StringComparison.Ordinal), needle + " is there twice");
            return at;
        }

        // ── the page ───────────────────────────────────────────────────

        /// <summary>The ids and names a test or a UI script finds Home's
        /// parts by. The card's type chip, the engine's card, the slot list
        /// and the Add Controller tile are the Dashboard's own, and #574
        /// keeps every one. The rest are new with the page.</summary>
        [Fact]
        public void Home_KeepsTheDashboardsIds_AndNamesItsNewParts()
        {
            string page = Home();
            foreach (string kept in new[]
            {
                "AutomationProperties.AutomationId=\"Dash_SlotTypeChip\"",
                "AutomationProperties.AutomationId=\"Dash_SlotTitle\"",
                "AutomationProperties.AutomationId=\"Dash_DeleteSlot\"",
                "AutomationProperties.AutomationId=\"Dash_StageChips\"",
                "<Border x:Name=\"EngineCard\"",
                "<ItemsControl x:Name=\"SlotsItemsControl\">",
                "<controls:ClickBorder x:Name=\"AddControllerCard\"",
                "<CollectionContainer x:Name=\"SlotsCollectionContainer\"/>",
            })
                At(page, kept);
            foreach (string added in new[]
            {
                "<TextBlock x:Name=\"HomeTitle\" AutomationProperties.HeadingLevel=\"Level1\"",
                "AutomationProperties.AutomationId=\"Home_EngineButton\"",
                "AutomationProperties.AutomationId=\"HomeOffer\"",
                "AutomationProperties.AutomationId=\"HomeOfferUse\"",
                "AutomationProperties.AutomationId=\"HomeOfferDismiss\"",
                "AutomationProperties.AutomationId=\"Home_ExtrasLink\"",
                "AutomationProperties.AutomationId=\"Home_OpenExtras\"",
            })
                At(page, added);

            // The window finds the page by the Dashboard's id, and its
            // sidebar entry by Nav_Dashboard, under the new tag.
            At(Window(), "<views:HomePage x:Name=\"HomePageView\" AutomationProperties.AutomationId=\"DashboardPageView\" Visibility=\"Visible\"/>");
            string code = WindowCode();
            At(code, "SetNavAutomationId(_navHome, \"Dashboard\");");
            At(code, "HomePageView.Visibility = tag == \"Home\" ? Visibility.Visible : Visibility.Collapsed;");
            Assert.DoesNotContain("\"Dashboard\"", code.Replace("SetNavAutomationId(_navHome, \"Dashboard\");", string.Empty));
            Assert.Contains("_selectedNavTag = \"Home\";", ExtrasPageTests.RepoText("PadForge.App", "ViewModels", "MainViewModel.cs"));
        }

        /// <summary>The engine's card: the flame is the switch it always was,
        /// the worded button beside the rate raises the same click, and the
        /// state reads as a polite live line with the counts of devices and
        /// controllers under it.</summary>
        [Fact]
        public void TheEngineCard_HasTheFlameAndAWordedButton_ForTheOneSwitch()
        {
            string page = Home();
            int card = At(page, "<Border x:Name=\"EngineCard\"");
            int offer = At(page, "<Border x:Name=\"HomeOffer\"");
            string engine = page.Substring(card, offer - card);
            Assert.Equal(2, Count(engine, "Click=\"EngineToggle_Click\""));
            Assert.Equal(2, Count(page, "Click=\"EngineToggle_Click\""));
            At(engine, "<controls:StateToggleButton x:Name=\"EngineFlame\" IsOn=\"{Binding IsEngineOn}\"");
            At(engine, "Content=\"{Binding EngineButtonText}\"");
            At(engine, "<TextBlock x:Name=\"EngineStatusLine\" common:LiveText.Polite=\"True\" Text=\"{Binding EngineStatus}\"");
            At(engine, "Text=\"{Binding PollingFrequencyText, Mode=OneWay}\"");
            At(engine, "<TextBlock x:Name=\"EngineDevicesLine\" Text=\"{Binding EngineLine}\"");
            // The flame and the button say the same thing on hover, in each state.
            foreach (string tip in new[] { "Dashboard_EngineFlame_Stopped", "Dashboard_EngineFlame_Running", "Dashboard_EngineFlame_Idle", "Common_Stopping" })
                Assert.Equal(2, Count(engine, "Value=\"{Binding " + tip + ", Source={x:Static strings:Strings.Instance}}\""));
        }

        /// <summary>The line under the engine's state counts the connected
        /// devices and the controllers, each a number after its label, and
        /// follows both counts.</summary>
        [Fact]
        public void TheEngineLine_CountsDevicesAndControllers_AndFollowsBoth()
        {
            var vm = new DashboardViewModel();
            var changed = new List<string>();
            vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            Assert.Equal(string.Format(Strings.Instance.Home_EngineLine_Format, 0, 0), vm.EngineLine);

            vm.OnlineDevices = 5;
            Assert.Contains(nameof(DashboardViewModel.EngineLine), changed);
            Assert.Equal(string.Format(Strings.Instance.Home_EngineLine_Format, 5, 0), vm.EngineLine);

            changed.Clear();
            vm.SlotSummaries.Add(new SlotSummary(0));
            vm.SlotSummaries.Add(new SlotSummary(1));
            Assert.Contains(nameof(DashboardViewModel.EngineLine), changed);
            Assert.Equal(string.Format(Strings.Instance.Home_EngineLine_Format, 5, 2), vm.EngineLine);

            changed.Clear();
            vm.SlotSummaries.RemoveAt(0);
            Assert.Contains(nameof(DashboardViewModel.EngineLine), changed);
            Assert.Equal(string.Format(Strings.Instance.Home_EngineLine_Format, 5, 1), vm.EngineLine);

            // The English names both numbers, so a line that dropped one would not pass above by accident.
            string english = Strings.Instance.Home_EngineLine_Format;
            Assert.Contains("{0}", english);
            Assert.Contains("{1}", english);
        }

        [Fact]
        public void TheEngineButton_SaysWhatAClickDoes()
        {
            var vm = new DashboardViewModel();
            var changed = new List<string>();
            vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

            vm.EngineStateKey = "Running";
            Assert.Equal(Strings.Instance.Common_Stop, vm.EngineButtonText);
            // Idle and stopping are still on: the button stops the engine.
            vm.EngineStateKey = "Idle";
            Assert.Equal(Strings.Instance.Common_Stop, vm.EngineButtonText);
            vm.EngineStateKey = "Stopping";
            Assert.Equal(Strings.Instance.Common_Stop, vm.EngineButtonText);

            changed.Clear();
            vm.EngineStateKey = "Stopped";
            Assert.Equal(Strings.Instance.Common_Start, vm.EngineButtonText);
            Assert.Contains(nameof(DashboardViewModel.IsEngineOn), changed);
            Assert.Contains(nameof(DashboardViewModel.EngineButtonText), changed);
        }

        /// <summary>A controller's card opens with its type chip, the type's
        /// name and which one of that type it is, and its flame in the
        /// corner. Under that line come the Preset, the devices that drive
        /// it, its state with the device counts, and the stage icons. The
        /// name is one of the seven type names, picked by the slot's output
        /// type.</summary>
        [Fact]
        public void AControllersCard_IsHeadedByItsTypeAndNumber_WithThePresetUnderIt()
        {
            string page = Home();
            int row0 = At(page, "<!-- Row 0: [type chip] Type name #N ... [flame] -->");
            int row1 = At(page, "<!-- Row 1: the Preset, what this controller");
            int row2 = At(page, "<!-- Row 2: the devices that drive it.");
            int row3 = At(page, "<!-- Row 3: the state, then how many devices are");
            int row4 = At(page, "<!-- Row 4: the stage icons (#175 item 10)");
            int delete = At(page, "<!-- Delete: a round button on the card's upper");
            Assert.True(row0 < row1 && row1 < row2 && row2 < row3 && row3 < row4 && row4 < delete);
            string head = page.Substring(row0, row1 - row0);
            int chip = At(head, "Click=\"TypeChip_Click\" Tag=\"{Binding PadIndex}\"");
            int name = At(head, "<Setter Property=\"Text\" Value=\"{Binding ControllerType_Xbox, Source={x:Static strings:Strings.Instance}}\"/>");
            int number = At(head, "<TextBlock Text=\"{Binding TypeInstanceLabel, StringFormat={}#{0}}\"");
            int flame = At(head, "IsOn=\"{Binding IsEnabled}\" Click=\"PowerToggle_Click\" Tag=\"{Binding PadIndex}\"");
            Assert.True(chip < name && name < number && number < flame, "the card's first line is out of order");
            // The flame is the line's last column, so it sits in the corner,
            // and the delete button is no part of the line.
            At(head, "<controls:StateToggleButton Grid.Column=\"2\" Width=\"32\" Height=\"32\" Margin=\"2,0,-6,0\"");
            Assert.Equal(3, Count(head.Substring(0, head.IndexOf("</Grid.ColumnDefinitions>", StringComparison.Ordinal)), "<ColumnDefinition "));
            Assert.DoesNotContain("DeleteSlot_Click", head);
            foreach (var (type, key) in new[]
            {
                ("PlayStation", "ControllerType_PlayStation"), ("Nintendo", "ControllerType_Nintendo"),
                ("Extended", "ControllerType_Extended"), ("KeyboardMouse", "ControllerType_KeyboardMouse"),
                ("Midi", "ControllerType_MIDI"), ("Vr", "ControllerType_VR"),
            })
            {
                Assert.Matches(new Regex("<DataTrigger Binding=\"\\{Binding OutputType\\}\" Value=\"" + type + "\">\\s*"
                    + "<Setter Property=\"Text\" Value=\"\\{Binding " + key + ", Source=\\{x:Static strings:Strings.Instance\\}\\}\"/>"), head);
                // The output type has a member of that name, so the trigger can match.
                Assert.True(Enum.IsDefined(typeof(VirtualControllerType), type), type + " is not an output type");
            }
            Assert.Equal(7, Enum.GetValues(typeof(VirtualControllerType)).Length);
            Assert.DoesNotContain("Dashboard_Slot,", page);
            Assert.DoesNotContain("{Binding SlotNumber}", page);

            string preset = page.Substring(row1, row2 - row1);
            At(preset, "<controls:MarqueeLabel Grid.Row=\"1\" Text=\"{Binding PresetName}\"");

            // The devices: one entry for each, with a bar ahead of every
            // entry but the first. The bar's trigger is in the bar's own
            // style, where the nearest ContentPresenter is the entry's.
            string devices = page.Substring(row2, row3 - row2);
            // The line's list and the tooltip's, which names every device.
            Assert.Equal(2, Count(devices, "<ItemsControl ItemsSource=\"{Binding MappedDevices}\""));
            At(devices, "AlternationCount=\"99\" Focusable=\"False\"");
            At(devices, "common:MarqueeBehavior.IsEnabled=\"True\"");
            Assert.Matches(new Regex("<TextBlock Text=\"  \\|  \" FontSize=\"13\"\\s+"
                + "Foreground=\"\\{DynamicResource TextFillColorTertiaryBrush\\}\">\\s*<TextBlock.Style>\\s*<Style TargetType=\"TextBlock\">\\s*<Style.Triggers>\\s*"
                + "<DataTrigger Binding=\"\\{Binding \\(ItemsControl.AlternationIndex\\), RelativeSource=\\{RelativeSource AncestorType=ContentPresenter\\}\\}\" Value=\"0\">\\s*"
                + "<Setter Property=\"Visibility\" Value=\"Collapsed\"/>"), devices);
            Assert.DoesNotContain("<DataTemplate.Triggers>", devices);

            // The state and the counts: one line while they fit the card,
            // the counts under the state when they do not, and the dot
            // between them only on the one line.
            string state = page.Substring(row3, row4 - row3);
            At(state, "<controls:LineOrStackPanel Grid.Row=\"3\" VerticalAlignment=\"Bottom\" Margin=\"0,10,0,0\">");
            At(state, "<TextBlock common:LiveText.Polite=\"True\" Text=\"{Binding StatusText}\" FontSize=\"13\"");
            At(state, "<TextBlock controls:LineOrStackPanel.OnLineOnly=\"True\" Text=\"·\"");
            foreach (string run in new[]
            {
                "<Run Text=\"{Binding MappedDeviceCount, Mode=OneWay}\"/>",
                "<Run Text=\"{Binding Dashboard_Mapped, Source={x:Static strings:Strings.Instance}, Mode=OneWay}\"/>",
                "<Run Text=\"{Binding ConnectedDeviceCount, Mode=OneWay}\"/>",
                "<Run Text=\"{Binding Dashboard_Connected, Source={x:Static strings:Strings.Instance}, Mode=OneWay}\"/>",
            })
                At(state, run);

            // The stage icons, drawn by the template the controller page's
            // header uses.
            string stages = page.Substring(row4, delete - row4);
            At(stages, "<ItemsControl Grid.Row=\"4\" ItemsSource=\"{Binding StageLedger}\"");
            At(stages, "ItemTemplate=\"{StaticResource StageChipTemplate}\"");

            // Delete: on the card's upper corner, over all five rows, last
            // in the grid, shown by the card's hover or its own keyboard
            // focus, and one click.
            string corner = page.Substring(delete, page.IndexOf("</DataTemplate>", delete, StringComparison.Ordinal) - delete);
            At(corner, "<ui:Button Grid.RowSpan=\"5\" Width=\"26\" Height=\"26\" Padding=\"0\" CornerRadius=\"13\"");
            At(corner, "HorizontalAlignment=\"Right\" VerticalAlignment=\"Top\" Margin=\"0,-28,-28,0\"");
            At(corner, "Click=\"DeleteSlot_Click\" Tag=\"{Binding PadIndex}\"");
            At(corner, "<Setter Property=\"Opacity\" Value=\"0\"/>");
            Assert.Matches(new Regex("<DataTrigger Binding=\"\\{Binding IsMouseOver, RelativeSource=\\{RelativeSource AncestorType=ContentPresenter\\}\\}\" Value=\"True\">\\s*"
                + "<Setter Property=\"Opacity\" Value=\"1\"/>"), corner);
            Assert.Matches(new Regex("<Trigger Property=\"IsKeyboardFocused\" Value=\"True\">\\s*<Setter Property=\"Opacity\" Value=\"1\"/>"), corner);
            Assert.Equal(1, Count(page, "DeleteSlot_Click"));
        }

        /// <summary>The margin that puts the delete button on the card's
        /// corner is the card's own numbers: its padding and edge, and half
        /// the button less the two it sits inside the corner.</summary>
        [Fact]
        public void TheDeleteButtonsMargin_IsTheCardsPaddingEdgeAndHalfTheButton()
        {
            string page = Home();
            var card = Regex.Match(page, "<Border MinHeight=\"156\" Padding=\"(\\d+)\" CornerRadius=\"10\" BorderThickness=\"(\\d+)\"");
            Assert.True(card.Success, "the card's opening tag changed");
            var button = Regex.Match(page, "<ui:Button Grid.RowSpan=\"5\" Width=\"(\\d+)\" Height=\"(\\d+)\" Padding=\"0\" CornerRadius=\"(\\d+)\"\\s+"
                + "HorizontalAlignment=\"Right\" VerticalAlignment=\"Top\" Margin=\"0,-(\\d+),-(\\d+),0\"");
            Assert.True(button.Success, "the delete button's opening tag changed");
            int padding = int.Parse(card.Groups[1].Value), edge = int.Parse(card.Groups[2].Value);
            int width = int.Parse(button.Groups[1].Value), height = int.Parse(button.Groups[2].Value);
            Assert.Equal(width, height);
            Assert.Equal(width / 2, int.Parse(button.Groups[3].Value));
            int margin = padding + edge + width / 2 - 2;
            Assert.Equal(margin, int.Parse(button.Groups[4].Value));
            Assert.Equal(margin, int.Parse(button.Groups[5].Value));
        }

        // ── the extras in use ──────────────────────────────────────────

        private static readonly Regex TileElement = new("<controls:ExtraTile\\b[^>]*?/>", RegexOptions.Singleline);
        private static readonly Regex Attribute = new("([\\w.:]+)=\"([^\"]*)\"");

        private static List<Dictionary<string, string>> Tiles(string page) =>
            TileElement.Matches(page)
                .Select(m => Attribute.Matches(m.Value).ToDictionary(a => a.Groups[1].Value, a => a.Groups[2].Value))
                .ToList();

        /// <summary>Home lists the extras by the catalog's own facts: the same
        /// ten keys in the same order, each with the catalog tile's glyph,
        /// title, line and status, and each showing while the switch that
        /// tile shows is on. A tile that read another switch would show an
        /// extra the catalog calls off.</summary>
        [Fact]
        public void TheExtrasInUse_AreTheCatalogsTen_EachByTheCatalogsOwnSwitch()
        {
            var catalog = Tiles(ExtrasPageTests.RepoText("PadForge.App", "Views", "ExtrasPage.xaml"));
            var rows = Tiles(Home());
            string[] keys = PadForge.Views.ExtrasPage.Keys.ToArray();
            Assert.Equal(DashboardViewModel.ExtrasCount, keys.Length);
            Assert.Equal(keys, catalog.Select(t => t["Key"]));
            Assert.Equal(keys, rows.Select(t => t["Key"]));

            for (int i = 0; i < keys.Length; i++)
            {
                var tile = catalog[i];
                var row = rows[i];
                Assert.Equal("{StaticResource HomeExtraTile}", row["Style"]);
                Assert.Equal(tile["Glyph"], row["Glyph"]);
                Assert.Equal(tile["Title"], row["Title"]);
                Assert.Equal(tile["Line"], row["Line"]);
                Assert.Equal("{Binding Extras_Tile_" + keys[i] + ", Source={x:Static strings:Strings.Instance}}", row["Line"]);
                Assert.Equal(tile.GetValueOrDefault("Status"), row.GetValueOrDefault("Status"));
                // The catalog's switch writes back, Home's tile only reads.
                string flag = Regex.Match(tile["IsOn"], "^\\{Binding (\\w+)\\}$").Groups[1].Value;
                Assert.False(string.IsNullOrEmpty(flag), keys[i] + "'s catalog tile has no plain IsOn binding");
                Assert.Equal("{Binding " + flag + ", Mode=OneWay}", row["IsOn"]);
                Assert.Equal("{Binding " + flag + ", Converter={StaticResource BoolToVisibilityConverter}}", row["Visibility"]);
                // Home's tile has no switch and no name of its own.
                Assert.False(row.ContainsKey("SwitchLabel") || row.ContainsKey("x:Name"));
                // One tile has a second button: the Web Controller's, to its QR code.
                if (keys[i] == "WebController")
                    Assert.Equal("{Binding Home_ShowQr, Source={x:Static strings:Strings.Instance}}", row["ActionText"]);
                else
                    Assert.False(row.ContainsKey("ActionText"), keys[i] + " has a second button");
            }

            // One handler for every tile, and two ways to the catalog: the
            // link in the line under the tiles, and the button that shows
            // while no extra is on.
            string page = Home();
            At(page, "<controls:TileGridPanel x:Name=\"ExtrasInUseRows\" MinItemWidth=\"280\" Gap=\"16\"");
            At(page, "controls:ExtraTile.OpenRequested=\"ExtraInUse_OpenRequested\">");
            Assert.Equal(2, Count(page, "Click=\"OpenExtras_Click\""));
            // The style gives a tile its own id, so the two faces of one
            // extra never share one, and draws no switch. Its second
            // button is there only for a tile that names one.
            string styles = ExtrasPageTests.RepoText("PadForge.App", "Resources", "ControllerIcons.xaml");
            int style = At(styles, "<Style x:Key=\"HomeExtraTile\" TargetType=\"{x:Type controls:ExtraTile}\">");
            string home = styles.Substring(style);
            At(home, "<Setter Property=\"OpenPrefix\" Value=\"Home_Extra_\"/>");
            Assert.Equal("Home_Extra_", PadForge.Controls.ExtraTile.HomeOpenIdPrefix);
            Assert.DoesNotContain("PART_Switch", home);
            At(home, "x:Name=\"PART_Open\"");
            At(home, "<Button x:Name=\"PART_Action\"");
            At(home, "Content=\"{TemplateBinding ActionText}\"");
            Assert.Matches(new Regex("<Trigger Property=\"ActionText\" Value=\"\">\\s*<Setter TargetName=\"PART_Action\" Property=\"Visibility\" Value=\"Collapsed\"/>"), home);
            Assert.Matches(new Regex("<Trigger Property=\"ActionText\" Value=\"\\{x:Null\\}\">\\s*<Setter TargetName=\"PART_Action\" Property=\"Visibility\" Value=\"Collapsed\"/>"), home);
            Assert.Equal("PART_Action", PadForge.Controls.ExtraTile.ActionPartName);
            // Under the name: the word On and then the status or the line.
            At(home, "<Binding Path=\"Summary\" RelativeSource=\"{RelativeSource TemplatedParent}\"/>");
            At(home, "<Binding Path=\"Home_ExtraOn_Format\" Source=\"{x:Static strings:Strings.Instance}\"/>");
        }

        /// <summary>The count Home reads is the ten catalog switches, one
        /// each. The heading and the line follow it: with none on the
        /// section introduces Extras, with some on it lists them and says
        /// where the rest are, and with all ten on the line is gone.</summary>
        [Fact]
        public void TheExtrasCount_FollowsEachOfTheTenSwitches()
        {
            bool udp = HeadTrackingRuntime.Enabled, free = HeadTrackingRuntime.FreeTrackEnabled, xr = HeadTrackingRuntime.OpenXrEnabled;
            bool telemetry = GameTelemetryRuntime.Enabled, twitch = TwitchChatRuntime.Enabled;
            try
            {
                var vm = new DashboardViewModel { DiscoverOpenXrRuntimes = () => Array.Empty<PadForge.Engine.Common.OpenXr.OpenXrRuntimeEntry>() };
                var switches = new (string Name, Action<bool> Set)[]
                {
                    (nameof(vm.EnableWebController), v => vm.EnableWebController = v),
                    (nameof(vm.HeadTrackingAnyEnabled), v => vm.HeadTrackingAnyEnabled = v),
                    (nameof(vm.EnableGameTelemetry), v => vm.EnableGameTelemetry = v),
                    (nameof(vm.EnableTwitchChat), v => vm.EnableTwitchChat = v),
                    (nameof(vm.EnableRemoteLink), v => vm.EnableRemoteLink = v),
                    (nameof(vm.EnableDsuMotionServer), v => vm.EnableDsuMotionServer = v),
                    (nameof(vm.EnableGameModServer), v => vm.EnableGameModServer = v),
                    (nameof(vm.OverlaysAnyEnabled), v => vm.OverlaysAnyEnabled = v),
                    (nameof(vm.EnableTouchpadOverlay), v => vm.EnableTouchpadOverlay = v),
                    (nameof(vm.ScreenOverlaysAnyEnabled), v => vm.ScreenOverlaysAnyEnabled = v),
                };
                Assert.Equal(DashboardViewModel.ExtrasCount, switches.Length);
                foreach (var s in switches) s.Set(false);
                Assert.Equal(0, vm.ExtrasInUseCount);
                Assert.True(vm.ShowsExtrasIntro);
                Assert.False(vm.HasExtrasInUse);
                Assert.False(vm.ShowsExtrasRestLine);

                // Each switch alone counts one, and tells the page.
                var changed = new List<string>();
                vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
                foreach (var s in switches)
                {
                    changed.Clear();
                    s.Set(true);
                    Assert.True(vm.ExtrasInUseCount == 1, s.Name + " on counts " + vm.ExtrasInUseCount);
                    foreach (string follower in new[]
                    {
                        nameof(vm.ExtrasInUseCount), nameof(vm.HasExtrasInUse), nameof(vm.ShowsExtrasRestLine), nameof(vm.ShowsExtrasIntro),
                    })
                        Assert.True(changed.Contains(follower), s.Name + " did not raise " + follower);
                    Assert.True(vm.HasExtrasInUse && vm.ShowsExtrasRestLine && !vm.ShowsExtrasIntro);
                    s.Set(false);
                    Assert.Equal(0, vm.ExtrasInUseCount);
                }

                // All ten: nothing is left to point at.
                foreach (var s in switches) s.Set(true);
                Assert.Equal(DashboardViewModel.ExtrasCount, vm.ExtrasInUseCount);
                Assert.True(vm.HasExtrasInUse);
                Assert.False(vm.ShowsExtrasRestLine);
                Assert.False(vm.ShowsExtrasIntro);
            }
            finally
            {
                HeadTrackingRuntime.Enabled = udp;
                HeadTrackingRuntime.FreeTrackEnabled = free;
                HeadTrackingRuntime.OpenXrEnabled = xr;
                GameTelemetryRuntime.Enabled = telemetry;
                TwitchChatRuntime.Enabled = twitch;
            }
        }

        /// <summary>The section has one heading, which changes its words
        /// with the count. Under the tiles, with some extras on and some
        /// off, a line says how many others there are, and the word that
        /// names the Extras page in it is a link to that page. With none
        /// on, the Extras page's own subtitle and a button to it take the
        /// line's place. The two never show at once: one follows
        /// ShowsExtrasRestLine and the other ShowsExtrasIntro, and the view
        /// model never raises both.</summary>
        [Fact]
        public void TheExtrasSection_HasOneHeading_AndALineWithALinkOrAnIntroWithAButton()
        {
            string page = Home();
            int heading = At(page, "<TextBlock x:Name=\"ExtrasHeading\"");
            int rows = At(page, "<controls:TileGridPanel x:Name=\"ExtrasInUseRows\"");
            int foot = At(page, "<StackPanel x:Name=\"ExtrasFoot\"");
            Assert.True(heading < rows && rows < foot);
            string head = page.Substring(heading, rows - heading);
            At(head, "<Style TargetType=\"TextBlock\" BasedOn=\"{StaticResource SectionTitle}\">");
            At(head, "<Setter Property=\"Text\" Value=\"{Binding Home_ExtrasInUse, Source={x:Static strings:Strings.Instance}}\"/>");
            Assert.Matches(new Regex("<DataTrigger Binding=\"\\{Binding ShowsExtrasIntro\\}\" Value=\"True\">\\s*"
                + "<Setter Property=\"Text\" Value=\"\\{Binding Extras_Title, "), head);

            string line = page.Substring(foot);
            const string rest = "Visibility=\"{Binding ShowsExtrasRestLine, Converter={StaticResource BoolToVisibilityConverter}}\"";
            const string intro = "Visibility=\"{Binding ShowsExtrasIntro, Converter={StaticResource BoolToVisibilityConverter}}\"";
            // The line: a lead, the link, a tail, on one line of markup so
            // no space comes between them.
            At(line, "<TextBlock x:Name=\"ExtrasLine\"");
            At(line, rest + "><Run Text=\"{Binding ExtrasRestLead, Mode=OneWay}\"/>"
                + "<Hyperlink x:Name=\"ExtrasLink\" Click=\"OpenExtras_Click\" Foreground=\"{DynamicResource EmberHotBrush}\" AutomationProperties.AutomationId=\"Home_ExtrasLink\">"
                + "<Run Text=\"{Binding ExtrasRestLink, Mode=OneWay}\"/></Hyperlink>"
                + "<Run Text=\"{Binding ExtrasRestTail, Mode=OneWay}\"/></TextBlock>");
            // The intro and its button show together.
            Assert.Equal(1, Count(line, rest));
            Assert.Equal(2, Count(line, intro));
            Assert.Contains(intro, Regex.Match(line, "<TextBlock x:Name=\"ExtrasIntroLine\"[^>]*/>", RegexOptions.Singleline).Value);
            Assert.Contains("Text=\"{Binding Extras_Subtitle, Source={x:Static strings:Strings.Instance}}\"",
                Regex.Match(line, "<TextBlock x:Name=\"ExtrasIntroLine\"[^>]*/>", RegexOptions.Singleline).Value);
            string button = Regex.Match(line, "<ui:Button x:Name=\"OpenExtrasButton\"[^>]*/>", RegexOptions.Singleline).Value;
            Assert.Contains(intro, button);
            Assert.Contains("Click=\"OpenExtras_Click\"", button);
        }

        /// <summary>The line's three parts come from one string: what stands
        /// before the bracketed word, the word, and what stands after. The
        /// number in it is how many extras are off. A translation with no
        /// brackets is shown whole, with no link.</summary>
        [Fact]
        public void TheExtrasLine_SplitsAtTheBracketedWord_AndCountsTheExtrasThatAreOff()
        {
            bool udp = HeadTrackingRuntime.Enabled, free = HeadTrackingRuntime.FreeTrackEnabled, xr = HeadTrackingRuntime.OpenXrEnabled;
            bool telemetry = GameTelemetryRuntime.Enabled, twitch = TwitchChatRuntime.Enabled;
            try
            {
                var vm = new DashboardViewModel { DiscoverOpenXrRuntimes = () => Array.Empty<PadForge.Engine.Common.OpenXr.OpenXrRuntimeEntry>() };
                foreach (Action<bool> set in new Action<bool>[]
                {
                    v => vm.EnableWebController = v, v => vm.HeadTrackingAnyEnabled = v, v => vm.EnableGameTelemetry = v,
                    v => vm.EnableTwitchChat = v, v => vm.EnableRemoteLink = v, v => vm.EnableDsuMotionServer = v,
                    v => vm.EnableGameModServer = v, v => vm.OverlaysAnyEnabled = v, v => vm.EnableTouchpadOverlay = v,
                    v => vm.ScreenOverlaysAnyEnabled = v,
                })
                    set(false);
                vm.EnableWebController = true;
                vm.EnableRemoteLink = true;
                vm.EnableTouchpadOverlay = true;
                Assert.Equal(3, vm.ExtrasInUseCount);

                string whole = string.Format(Strings.Instance.Home_MoreExtras, DashboardViewModel.ExtrasCount - 3);
                Assert.Contains("7", whole);
                int open = whole.IndexOf('['), close = whole.IndexOf(']');
                Assert.True(open >= 0 && close > open + 1, "the English line has no bracketed word");
                Assert.Equal(whole.Substring(0, open), vm.ExtrasRestLead);
                Assert.Equal(whole.Substring(open + 1, close - open - 1), vm.ExtrasRestLink);
                Assert.Equal(whole.Substring(close + 1), vm.ExtrasRestTail);
                Assert.Equal(Strings.Instance.Extras_Title, vm.ExtrasRestLink);
                Assert.Equal(whole.Replace("[", string.Empty).Replace("]", string.Empty),
                    vm.ExtrasRestLead + vm.ExtrasRestLink + vm.ExtrasRestTail);

                // A switch that changes the count tells the three parts.
                var changed = new List<string>();
                vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
                vm.EnableTwitchChat = true;
                foreach (string part in new[] { nameof(vm.ExtrasRestLead), nameof(vm.ExtrasRestLink), nameof(vm.ExtrasRestTail) })
                    Assert.Contains(part, changed);
                Assert.Contains("6", vm.ExtrasRestLead + vm.ExtrasRestTail);
            }
            finally
            {
                HeadTrackingRuntime.Enabled = udp;
                HeadTrackingRuntime.FreeTrackEnabled = free;
                HeadTrackingRuntime.OpenXrEnabled = xr;
                GameTelemetryRuntime.Enabled = telemetry;
                TwitchChatRuntime.Enabled = twitch;
            }
        }

        /// <summary>The line's split, on any string: with one pair of
        /// brackets it is three parts, and with none, or with a pair left
        /// open, the whole string is the lead and nothing is a link.</summary>
        [Theory]
        [InlineData("7 more in [Extras]. An extra is off.", "7 more in ", "Extras", ". An extra is off.")]
        [InlineData("[Extras] has 7 more.", "", "Extras", " has 7 more.")]
        [InlineData("7 more in [Extras]", "7 more in ", "Extras", "")]
        [InlineData("7 more in Extras.", "7 more in Extras.", "", "")]
        [InlineData("7 more in [Extras.", "7 more in [Extras.", "", "")]
        [InlineData("7 more in Extras].", "7 more in Extras].", "", "")]
        [InlineData("", "", "", "")]
        public void TheSplit_IsThreePartsAroundOnePairOfBrackets(string text, string lead, string link, string tail)
        {
            Assert.Equal(lead, DashboardViewModel.BracketPart(text, 0));
            Assert.Equal(link, DashboardViewModel.BracketPart(text, 1));
            Assert.Equal(tail, DashboardViewModel.BracketPart(text, 2));
        }

        // ── the offer's rule ───────────────────────────────────────────

        [Theory]
        // Everything in place: offered.
        [InlineData(true, true, true, true, false, true)]
        // The prompt is off.
        [InlineData(false, true, true, true, false, false)]
        // The device has had its answer, or was known before version 6.
        [InlineData(true, false, true, true, false, false)]
        // It is not connected.
        [InlineData(true, true, false, true, false, false)]
        // It is no game controller.
        [InlineData(true, true, true, false, false, false)]
        // A virtual controller already uses it.
        [InlineData(true, true, true, true, true, false)]
        public void Offer_Decision(bool enabled, bool pending, bool online, bool eligible, bool assigned, bool expected)
        {
            Assert.Equal(expected, InputService.HomeOfferDecision(enabled, pending, online, eligible, assigned));
        }

        [Theory]
        [InlineData(InputDeviceType.Joystick, true)]
        [InlineData(InputDeviceType.Gamepad, true)]
        [InlineData(InputDeviceType.Driving, true)]
        [InlineData(InputDeviceType.Flight, true)]
        [InlineData(InputDeviceType.FirstPerson, true)]
        [InlineData(InputDeviceType.Supplemental, true)]
        // Desktop peripherals, which the pad page's offer leaves out too.
        [InlineData(InputDeviceType.Keyboard, false)]
        [InlineData(InputDeviceType.Mouse, false)]
        [InlineData(InputDeviceType.Touchpad, false)]
        [InlineData(InputDeviceType.ConsumerControl, false)]
        // Rows the pad page's offer takes and Home's does not: Home's makes
        // a virtual controller for the device, the wrong thing to say of these.
        [InlineData(InputDeviceType.Midi, false)]
        [InlineData(InputDeviceType.Nfc, false)]
        [InlineData(InputDeviceType.HeadsetMotion, false)]
        [InlineData(InputDeviceType.Microphone, false)]
        [InlineData(InputDeviceType.HandheldButtons, false)]
        [InlineData(InputDeviceType.SystemMotion, false)]
        [InlineData(InputDeviceType.HeadTracker, false)]
        [InlineData(InputDeviceType.Tablet, false)]
        [InlineData(InputDeviceType.VrController, false)]
        [InlineData(InputDeviceType.LogitechGKeys, false)]
        [InlineData(InputDeviceType.AnalogKeyboard, false)]
        [InlineData(InputDeviceType.WebMenus, false)]
        public void Offer_IsForGameControllersOnly(int capType, bool expected)
        {
            var ud = new UserDevice { InstanceGuid = Guid.NewGuid(), CapType = capType, ProductName = "X", InstanceName = "X" };
            Assert.Equal(expected, InputService.IsHomeOfferEligible(ud));
            Assert.False(InputService.IsHomeOfferEligible(null));
            Assert.False(InputService.IsHomeOfferEligible(new UserDevice { CapType = capType }));
        }

        /// <summary>What a device still owes is saved with the device. A
        /// device from a file written before version 6 has no such line and
        /// reads as owing nothing, so an upgrade offers nothing for a room
        /// of devices left unassigned on purpose.</summary>
        [Fact]
        public void WhatADeviceOwes_IsSavedWithIt_AndAnOlderFileOwesNothing()
        {
            var ser = new XmlSerializer(typeof(UserDevice));
            string Xml(UserDevice d) { var sw = new StringWriter(); ser.Serialize(sw, d); return sw.ToString(); }
            UserDevice Read(string xml) => (UserDevice)ser.Deserialize(new StringReader(xml));

            Assert.False(new UserDevice().HomeOfferPending);
            string owed = Xml(new UserDevice { InstanceGuid = Guid.NewGuid(), HomeOfferPending = true });
            Assert.Contains("<HomeOfferPending>true</HomeOfferPending>", owed);
            Assert.True(Read(owed).HomeOfferPending);

            string settled = Xml(new UserDevice { InstanceGuid = Guid.NewGuid() });
            Assert.DoesNotContain("HomeOfferPending", settled);
            Assert.False(Read(settled).HomeOfferPending);
        }

        /// <summary>The registry marks a device as owed where it makes one it
        /// has never met, in its two such places, and nowhere else: a row a
        /// settings file brings is a device already known.</summary>
        [Fact]
        public void OnlyADeviceTheRegistryMakes_IsOwedTheOffer()
        {
            string root = Path.GetDirectoryName(FindSolution())!;
            var setters = new List<string>();
            foreach (string project in new[] { "PadForge.App", "PadForge.Engine" })
                foreach (string file in Directory.GetFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                    if (rel.Contains("/obj/") || rel.Contains("/bin/")) continue;
                    int n = Count(File.ReadAllText(file), "HomeOfferPending = true");
                    for (int i = 0; i < n; i++) setters.Add(rel);
                }
            Assert.Equal(new[]
            {
                "PadForge.App/Common/Input/InputManager.Step1.UpdateDevices.cs",
                "PadForge.App/Common/Input/InputManager.Step1.UpdateDevices.cs",
            }, setters);
        }

        private static string FindSolution()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return Path.Combine(dir!.FullName, "PadForge.sln");
        }

        // ── the offer on the page ──────────────────────────────────────

        [Fact]
        public void TheOffer_IsSetClearedAndDeclined()
        {
            var vm = new DashboardViewModel();
            var changed = new List<string>();
            vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            Assert.False(vm.HasHomeOffer);
            Assert.Equal(string.Empty, vm.HomeOfferText);

            var g = Guid.NewGuid();
            vm.SetHomeOffer(g, "Switch Pro Controller");
            Assert.True(vm.HasHomeOffer);
            Assert.Equal(g, vm.HomeOfferGuid);
            Assert.Equal(string.Format(Strings.Instance.Home_Offer_Format, "Switch Pro Controller"), vm.HomeOfferText);
            Assert.Contains(nameof(DashboardViewModel.HasHomeOffer), changed);
            Assert.Contains(nameof(DashboardViewModel.HomeOfferText), changed);

            // Not Now: the offer goes first, then the listener hears which device.
            Guid declined = Guid.Empty;
            bool stillShown = true;
            vm.HomeOfferDismissed += (_, x) => { declined = x; stillShown = vm.HasHomeOffer; };
            vm.DismissHomeOfferCommand.Execute(null);
            Assert.Equal(g, declined);
            Assert.False(stillShown);
            Assert.False(vm.HasHomeOffer);
            Assert.Equal(string.Empty, vm.HomeOfferText);

            // With no offer, Not Now does nothing.
            declined = Guid.Empty;
            vm.DismissHomeOfferCommand.Execute(null);
            Assert.Equal(Guid.Empty, declined);

            // An empty guid is a clear, not an offer.
            vm.SetHomeOffer(g, "X");
            vm.SetHomeOffer(Guid.Empty, "X");
            Assert.False(vm.HasHomeOffer);
        }

        private static UserDevice Device(string name, int capType = InputDeviceType.Gamepad, bool online = true, bool pending = true)
        {
            var ud = new UserDevice
            {
                InstanceGuid = Guid.NewGuid(),
                ProductName = name,
                InstanceName = name,
                CapType = capType,
                IsOnline = online,
                HomeOfferPending = pending,
            };
            lock (SettingsManager.UserDevices.SyncRoot)
                SettingsManager.UserDevices.Items.Add(ud);
            return ud;
        }

        private static (MainViewModel vm, InputService svc) Arrange()
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            var vm = new MainViewModel();
            return (vm, new InputService(vm));
        }

        /// <summary>The walk over the registry: the first device that is
        /// owed, connected, a game controller and unused is offered by its
        /// name, and it stays the one offered while it qualifies.</summary>
        [Fact]
        public void TheWalk_OffersTheFirstDeviceStillOwed_AndKeepsItStanding()
        {
            var (vm, svc) = Arrange();
            var known = Device("Known Pad", pending: false);
            var away = Device("Away Pad", online: false);
            var first = Device("First Pad");
            var second = Device("Second Pad");

            svc.EvaluateHomeOffer();
            Assert.True(vm.Dashboard.HasHomeOffer);
            Assert.Equal(first.InstanceGuid, vm.Dashboard.HomeOfferGuid);
            Assert.Contains("First Pad", vm.Dashboard.HomeOfferText);

            // A device ahead of it in the registry comes back: the offer on
            // screen does not jump to it.
            away.IsOnline = true;
            svc.EvaluateHomeOffer();
            Assert.Equal(first.InstanceGuid, vm.Dashboard.HomeOfferGuid);

            // Nothing is settled by being offered or by waiting.
            Assert.True(first.HomeOfferPending && second.HomeOfferPending && away.HomeOfferPending);
            Assert.False(known.HomeOfferPending);
        }

        /// <summary>The offer goes when its device disconnects, and what the
        /// device owes stays, so it is offered again when it is back. The
        /// next device still owed takes the offer's place.</summary>
        [Fact]
        public void TheWalk_WithdrawsTheOfferOfADeviceThatLeft_AndOffersTheNext()
        {
            var (vm, svc) = Arrange();
            var first = Device("First Pad");
            svc.EvaluateHomeOffer();
            Assert.Equal(first.InstanceGuid, vm.Dashboard.HomeOfferGuid);

            first.IsOnline = false;
            svc.EvaluateHomeOffer();
            Assert.False(vm.Dashboard.HasHomeOffer);
            Assert.True(first.HomeOfferPending);

            var second = Device("Second Pad");
            svc.EvaluateHomeOffer();
            Assert.Equal(second.InstanceGuid, vm.Dashboard.HomeOfferGuid);

            first.IsOnline = true;
            second.IsOnline = false;
            svc.EvaluateHomeOffer();
            Assert.Equal(first.InstanceGuid, vm.Dashboard.HomeOfferGuid);
        }

        /// <summary>A device on a virtual controller has its answer, however
        /// it got there, and a connected row that is no game controller owes
        /// nothing. Both are settled for good. An unconnected row of unknown
        /// kind keeps what it owes until it connects.</summary>
        [Fact]
        public void TheWalk_SettlesADeviceInUse_AndARowThatIsNoController()
        {
            var (vm, svc) = Arrange();
            var used = Device("Used Pad");
            var keyboard = Device("Some Keyboard", InputDeviceType.Keyboard);
            var microphone = Device("Some Microphone", InputDeviceType.Microphone);
            var asleep = Device("Asleep Row", InputDeviceType.Microphone, online: false);
            lock (SettingsManager.UserSettings.SyncRoot)
                SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = used.InstanceGuid, MapTo = 0 });

            svc.EvaluateHomeOffer();
            Assert.False(vm.Dashboard.HasHomeOffer);
            Assert.False(used.HomeOfferPending);
            Assert.False(keyboard.HomeOfferPending);
            Assert.False(microphone.HomeOfferPending);
            Assert.True(asleep.HomeOfferPending);

            // Taken off its virtual controller later, it is not offered: it had its answer.
            lock (SettingsManager.UserSettings.SyncRoot)
                SettingsManager.UserSettings.Items.Clear();
            svc.EvaluateHomeOffer();
            Assert.False(vm.Dashboard.HasHomeOffer);
        }

        /// <summary>The switch under Settings, Assignment Prompts: off takes
        /// the offer off the page and settles nothing, and on brings back
        /// the one still owed.</summary>
        [Fact]
        public void TheWalk_FollowsThePromptsSwitch_AndSettlesNothingWhileItIsOff()
        {
            var (vm, svc) = Arrange();
            var pad = Device("First Pad");
            Assert.True(vm.Settings.AssignOfferHome);
            svc.EvaluateHomeOffer();
            Assert.True(vm.Dashboard.HasHomeOffer);

            vm.Settings.AssignOfferHome = false;
            svc.EvaluateHomeOffer();
            Assert.False(vm.Dashboard.HasHomeOffer);
            Assert.True(pad.HomeOfferPending);

            vm.Settings.AssignOfferHome = true;
            svc.EvaluateHomeOffer();
            Assert.Equal(pad.InstanceGuid, vm.Dashboard.HomeOfferGuid);

            // Between device walks the window asks for the re-read, which
            // does nothing while the engine is stopped: Stop took the offer
            // down, and nothing reads which devices are connected.
            vm.Dashboard.ClearHomeOffer();
            svc.ReEvaluateHomeOffer();
            Assert.False(vm.Dashboard.HasHomeOffer);
        }

        /// <summary>The offer's wiring, which has no seam without a running
        /// engine: the walk runs after the pad pages' own on every device
        /// update, Not Now reaches the service only while the engine runs,
        /// and a stop takes the offer down.</summary>
        [Fact]
        public void TheOffer_IsWiredToTheDeviceWalk_TheEngineAndTheWindow()
        {
            string svc = ExtrasPageTests.RepoText("PadForge.App", "Services", "InputService.cs");
            int pads = svc.IndexOf("EvaluateAssignOffers();", StringComparison.Ordinal);
            int home = svc.IndexOf("EvaluateHomeOffer();", pads, StringComparison.Ordinal);
            Assert.True(pads > 0 && home > pads && home - pads < 400, "Home's walk does not follow the pad pages'");
            At(svc, "_mainVm.Dashboard.HomeOfferDismissed += OnHomeOfferDismissed;");
            Assert.Equal(2, Count(svc, "_mainVm.Dashboard.HomeOfferDismissed -= OnHomeOfferDismissed;"));
            At(svc, "_mainVm.Dashboard.ClearHomeOffer();");
            At(svc, "if (ud != null && ud.InstanceGuid == guid) ud.HomeOfferPending = false;");

            string code = WindowCode();
            // Use It opens the type bar under its own button, for that device.
            At(code, "ShowControllerTypePopup(args.Anchor, PlacementMode.Bottom, deviceForNewSlot: args.Device);");
            // All seven types share one click, and it is the only place the
            // bar makes a slot: the new slot takes the device, then the
            // offer is read again.
            int bar = At(code, "private void ShowControllerTypePopup(UIElement anchor, PlacementMode placement = PlacementMode.Right,");
            int end = At(code, "private void AddRetypeButtons(");
            string method = code.Substring(bar, end - bar);
            Assert.Equal(7, Count(method, "CreateFromBar(VirtualControllerType."));
            foreach (string type in Enum.GetNames(typeof(VirtualControllerType)))
                At(method, "CreateFromBar(VirtualControllerType." + type + ");");
            int create = At(method, "int newSlot = _deviceService.CreateSlot(type);");
            int assign = At(method, "_deviceService.AssignDeviceToSlot(deviceForNewSlot, newSlot);");
            int reread = At(method, "_inputService.ReEvaluateHomeOffer();");
            Assert.True(create < assign && assign < reread);
            // The switch is followed at once.
            Assert.Matches(new Regex("if \\(e\\.PropertyName == nameof\\(SettingsViewModel\\.AssignOfferHome\\)\\)\\s*"
                + "_inputService\\?\\.ReEvaluateHomeOffer\\(\\);"), code);

            string page = Home();
            At(page, "Visibility=\"{Binding HasHomeOffer, Converter={StaticResource BoolToVisibilityConverter}}\"");
            At(page, "<TextBlock x:Name=\"HomeOfferLine\" Grid.Column=\"1\" common:LiveText.Polite=\"True\"");
            At(page, "Click=\"HomeOfferUse_Click\"");
            At(page, "Command=\"{Binding DismissHomeOfferCommand}\"");
            At(page, "Content=\"{Binding Pad_AssignOffer_Dismiss, Source={x:Static strings:Strings.Instance}}\"");

            // The Settings card has the third switch, with its reset.
            string settings = ExtrasPageTests.RepoText("PadForge.App", "Views", "SettingsPage.xaml");
            At(settings, "IsChecked=\"{Binding AssignOfferHome}\"");
            At(settings, "AutomationProperties.AutomationId=\"Reset_SettingsViewModel_AssignOfferHome\"");
        }

        // ── the shell ──────────────────────────────────────────────────

        /// <summary>The sidebar reads Home, Devices, Profiles, Extras, then
        /// the word Controllers and the slots under it. The label is a text
        /// among the entries, kept by the rebuild of the slots and renamed
        /// with the language. The sidebar is always open at one width: it
        /// has no button that closes it, and nothing in the window reads
        /// or sets whether it is open.</summary>
        [Fact]
        public void TheSidebar_ReadsHomeDevicesProfilesExtras_ThenTheControllers()
        {
            string code = WindowCode();
            int last = -1;
            foreach (string needle in new[]
            {
                "NavView.MenuItems.Add(_navHome);",
                "NavView.MenuItems.Add(_navDevices);",
                "NavView.MenuItems.Add(_navProfiles);",
                "NavView.MenuItems.Add(_navExtras);",
                "NavView.MenuItems.Add(_navControllersLabel);",
            })
            {
                int at = At(code, needle);
                Assert.True(at > last, needle + " is out of order");
                last = at;
            }
            Assert.True(code.IndexOf("RebuildControllerSection();", last, StringComparison.Ordinal) > last);
            // Five entries stay through a rebuild of the slots.
            At(code, "private const int ControllerInsertIndex = 5;");
            At(code, "while (NavView.MenuItems.Count > ControllerInsertIndex)");
            At(code, "Content = Strings.Instance.Home_Title,");
            At(code, "if (_navHome != null) _navHome.Content = Strings.Instance.Home_Title;");
            At(code, "Text = Strings.Instance.Home_Controllers,");
            At(code, "if (_navControllersLabel != null) _navControllersLabel.Text = Strings.Instance.Home_Controllers;");

            // Always open: no toggle in the markup, no pane state in the code.
            string xaml = Window();
            At(xaml, "IsPaneToggleVisible=\"False\"");
            At(xaml, "OpenPaneLength=\"248\"");
            Assert.DoesNotContain("PaneToggleBtn", xaml);
            Assert.DoesNotContain("IsPaneOpen", xaml);
            foreach (string gone in new[] { "IsPaneOpen", "PaneClosed", "PaneOpened", "CompactPaneLength", "PaneToggleBtn" })
                Assert.DoesNotContain(gone, code);

            // The sidebar is drawn by the two templates of Resources/Shell.xaml,
            // which hold the three parts wpf-ui's NavigationView looks for.
            At(xaml, "Template=\"{StaticResource PadNavigationViewTemplate}\"");
            At(xaml, "ItemTemplate=\"{StaticResource PadNavigationViewItemTemplate}\"");
            string shell = ExtrasPageTests.RepoText("PadForge.App", "Resources", "Shell.xaml");
            At(shell, "<ControlTemplate x:Key=\"PadNavigationViewTemplate\" TargetType=\"{x:Type ui:NavigationView}\">");
            At(shell, "<ControlTemplate x:Key=\"PadNavigationViewItemTemplate\" TargetType=\"{x:Type ui:NavigationViewItem}\">");
            // Both lists are NavItemsControls, whose entries offer Invoke
            // and SelectionItem to UI Automation.
            int menu = At(shell, "<controls:NavItemsControl x:Name=\"PART_MenuItemsItemsControl\"");
            int footer = At(shell, "<controls:NavItemsControl x:Name=\"PART_FooterMenuItemsItemsControl\"");
            Assert.DoesNotContain("<ItemsControl x:Name=\"PART_", shell);
            int paneFooter = At(shell, "<ContentPresenter Content=\"{TemplateBinding PaneFooter}\"/>");
            At(shell, "<ui:NavigationViewContentPresenter x:Name=\"PART_NavigationViewContentPresenter\"");
            Assert.True(menu < footer && footer < paneFooter, "the sidebar's three lists are out of order");
            At(shell, "Width=\"{TemplateBinding OpenPaneLength}\"");
            // The hairline over Settings is the template's, between the two
            // lists, and no entry of either: an entry there was an unnamed
            // item to a screen reader.
            int hairline = At(shell, "<Border Height=\"1\" Margin=\"4,14,4,10\" Background=\"{DynamicResource SteelPaneLineBrush}\"/>");
            Assert.True(menu < hairline && hairline < footer);
            int items = At(xaml, "<ui:NavigationView.FooterMenuItems>");
            int itemsEnd = At(xaml, "</ui:NavigationView.FooterMenuItems>");
            string footerItems = xaml.Substring(items, itemsEnd - items);
            Assert.Equal(2, Count(footerItems, "<ui:NavigationViewItem x:Name=\""));
            Assert.DoesNotContain("<Border", footerItems);
            Assert.DoesNotContain("Separator", footerItems);
        }

        /// <summary>A controller's pill and the Add Controller entry draw
        /// their own outline and fill, so their entries clear the four row
        /// fills the entry template reads. What sits inside such an entry
        /// finds those keys clear too, so the fill a pill takes under the
        /// pointer has a key of its own, which the theme gives the row's
        /// color on both grounds.</summary>
        [Fact]
        public void APillsEntry_ClearsTheRowFills_AndThePillLiftsWithItsOwnKey()
        {
            string code = WindowCode();
            int clears = At(code, "private static void SuppressBuiltInSelectionVisual(NavigationViewItem item)");
            string method = code.Substring(clears, code.IndexOf("        }", clears, StringComparison.Ordinal) - clears);
            string[] rowKeys =
            {
                "NavigationViewItemBackground", "NavigationViewItemBackgroundSelected",
                "NavigationViewItemBackgroundPointerOver", "NavigationViewItemBackgroundPressed",
            };
            foreach (string key in rowKeys)
                At(method, "item.Resources[\"" + key + "\"] = clear;");
            Assert.Equal(rowKeys.Length, Count(method, "item.Resources["));

            // The entry template reads exactly those four.
            string shell = ExtrasPageTests.RepoText("PadForge.App", "Resources", "Shell.xaml");
            var read = Regex.Matches(shell, "\\{DynamicResource (NavigationView\\w+)\\}").Select(m => m.Groups[1].Value)
                .Where(k => k.StartsWith("NavigationViewItemBackground", StringComparison.Ordinal)).Distinct().OrderBy(k => k, StringComparer.Ordinal);
            Assert.Equal(rowKeys.OrderBy(k => k, StringComparer.Ordinal), read);

            // Nothing a pill draws reads a key its own entry clears.
            Assert.Equal(1, Count(code, "\"NavigationViewItemBackgroundPointerOver\""));
            Assert.Equal(3, Count(code, "\"SteelPaneHoverBrush\""));
            string theme = ExtrasPageTests.RepoText("PadForge.App", "Common", "EmberTheme.cs");
            At(theme, "SetBrush(\"NavigationViewItemBackgroundPointerOver\", paneHover);");
            At(theme, "SetBrush(\"SteelPaneHoverBrush\", paneHover);");
            At(ExtrasPageTests.RepoText("PadForge.App", "App.xaml"), "<SolidColorBrush x:Key=\"SteelPaneHoverBrush\" Color=\"#121925\"/>");
        }

        /// <summary>The status bar is gone. Its flame, state and rate are the
        /// sidebar's foot, its profile pill is in the title bar under the id
        /// it had, and its message is a line over the page's lower corner
        /// that is there only while there is a message.</summary>
        [Fact]
        public void TheStatusBarsParts_AreTheSidebarsFoot_TheTitleBarsPill_AndAMessageLine()
        {
            string xaml = Window();
            Assert.DoesNotContain("StatusBarBorder", xaml);
            Assert.DoesNotContain("StatusProfilePill", xaml);
            Assert.DoesNotContain("Grid.RowSpan=\"3\"", xaml);
            Assert.DoesNotContain("Main_DevicesSuffix", xaml);
            string code = WindowCode();
            Assert.DoesNotContain("StatusBarBorder", code);
            Assert.DoesNotContain("StatusProfilePill", code);
            // The window's root grid: the title bar's row and the rest.
            int rows = xaml.IndexOf("<Grid.RowDefinitions>", StringComparison.Ordinal);
            int rowsEnd = xaml.IndexOf("</Grid.RowDefinitions>", rows, StringComparison.Ordinal);
            Assert.Equal(2, Count(xaml.Substring(rows, rowsEnd - rows), "<RowDefinition "));
            Assert.Equal(5, Count(xaml, "Grid.RowSpan=\"2\""));

            // The foot.
            int foot = At(xaml, "<ui:NavigationView.PaneFooter>");
            int footEnd = At(xaml, "</ui:NavigationView.PaneFooter>");
            string engine = xaml.Substring(foot, footEnd - foot);
            At(engine, "<Border x:Name=\"EngineFoot\"");
            At(engine, "<Path x:Name=\"EngineFootFlame\" FlowDirection=\"LeftToRight\" Data=\"{StaticResource FlameOuterGeometry}\"");
            At(engine, "<TextBlock x:Name=\"EngineFootState\" Grid.Column=\"1\" Text=\"{Binding EngineStatusText}\"");
            // The rate is the text Home's engine card prints, so the unit
            // is the language's own in both places.
            At(engine, "<TextBlock x:Name=\"EngineFootRate\" Grid.Column=\"2\" VerticalAlignment=\"Center\"");
            At(engine, "Text=\"{Binding Dashboard.PollingFrequencyText, Mode=OneWay}\"");
            Assert.DoesNotContain(" Hz", engine);
            foreach (string state in new[] { "Running", "Idle", "Stopping" })
                Assert.Contains("<DataTrigger Binding=\"{Binding Dashboard.EngineStateKey}\" Value=\"" + state + "\">", engine);
            // The foot reads the window's view model, handed to it by name.
            At(code, "EngineFoot.DataContext = _viewModel;");

            // The pill: the word Profile, the name and a chevron in one
            // outlined box, with no switch that hides the word.
            int trailing = At(xaml, "<ui:TitleBar.TrailingContent>");
            int trailingEnd = At(xaml, "</ui:TitleBar.TrailingContent>");
            string bar = xaml.Substring(trailing, trailingEnd - trailing);
            int pill = At(bar, "<viewControls:ProfilePill x:Name=\"TitleProfilePill\"");
            At(bar, "AutomationProperties.AutomationId=\"ProfilePillStatusBar\"");
            Assert.True(pill < At(bar, "x:Name=\"FullScreenBtn\""), "the pill is not ahead of the full screen button");
            Assert.Equal(1, Count(xaml, "<viewControls:ProfilePill "));
            At(code, "ShowProfileSwitcherPopup(TitleProfilePill, PlacementMode.Bottom);");
            At(code, "TitleProfilePill.Flare();");
            string pillXaml = ExtrasPageTests.RepoText("PadForge.App", "Views", "Controls", "ProfilePill.xaml");
            int box = At(pillXaml, "<controls:ClickBorder x:Name=\"PillBorder\" Height=\"34\" CornerRadius=\"8\" BorderThickness=\"1\"");
            int label = At(pillXaml, "<TextBlock Text=\"{Binding Main_ProfileLabel, Source={x:Static strings:Strings.Instance}}\"");
            int name = pillXaml.LastIndexOf("<TextBlock Text=\"{Binding ActiveProfileInfo}\"", StringComparison.Ordinal);
            int chevron = At(pillXaml, "<Path FlowDirection=\"LeftToRight\" Data=\"M0,0 L5,5 L10,0\"");
            Assert.True(box < label && label < name && name < chevron, "the pill's parts are out of order");
            Assert.DoesNotContain("ShowLabel", pillXaml);
            Assert.DoesNotContain("ShowLabel", ExtrasPageTests.RepoText("PadForge.App", "Views", "Controls", "ProfilePill.xaml.cs"));
            Assert.DoesNotContain("ShowLabel", xaml);

            // The message line: first in the pages' grid, over them, out of
            // the pointer's way, and collapsed while there is no message.
            int overlay = At(xaml, "<ui:NavigationView.ContentOverlay>");
            int line = At(xaml, "<Border x:Name=\"StatusMessageLine\" Panel.ZIndex=\"10\"");
            int homeView = At(xaml, "<views:HomePage x:Name=\"HomePageView\"");
            Assert.True(overlay < line && line < homeView);
            Assert.DoesNotContain("<views:", xaml.Substring(overlay, line - overlay));
            string message = xaml.Substring(line, homeView - line);
            At(message, "IsHitTestVisible=\"False\"");
            At(message, "HorizontalAlignment=\"Left\" VerticalAlignment=\"Bottom\"");
            Assert.Matches(new Regex("<DataTrigger Binding=\"\\{Binding StatusText\\}\" Value=\"\">\\s*"
                + "<Setter Property=\"Visibility\" Value=\"Collapsed\"/>"), message);
            Assert.Matches(new Regex("<DataTrigger Binding=\"\\{Binding StatusText\\}\" Value=\"\\{x:Null\\}\">\\s*"
                + "<Setter Property=\"Visibility\" Value=\"Collapsed\"/>"), message);
            Assert.Matches(new Regex("<TextBlock x:Name=\"StatusMessageText\" AutomationProperties\\.LiveSetting=\"Polite\"\\s+"
                + "Text=\"\\{Binding StatusText\\}\""), message);
            // The fade is the line's, and a message is still announced from its text.
            Assert.Equal(2, Count(code, "StatusMessageLine.BeginAnimation(UIElement.OpacityProperty, null);"));
            At(code, "StatusMessageLine.BeginAnimation(UIElement.OpacityProperty, fade);");
            Assert.DoesNotContain("StatusMessageText.Opacity", code);
            At(code, "private void AnnounceStatus() => PadForge.Common.LiveText.Announce(StatusMessageText);");
        }

        /// <summary>Minimize, maximize, close and full screen are one row:
        /// the same size, the same place in the bar's height and the same
        /// gap. wpf-ui draws its three 44 by 30 against the bar's top, so
        /// the title bar's own resources restyle them, and the full screen
        /// button beside them carries the same numbers itself. The bar has
        /// no button that folds the sidebar.</summary>
        [Fact]
        public void TheFourWindowButtons_ShareOneSizeAndPlace()
        {
            string xaml = Window();
            int resources = At(xaml, "<ui:TitleBar.Resources>");
            int resourcesEnd = At(xaml, "</ui:TitleBar.Resources>");
            string style = xaml.Substring(resources, resourcesEnd - resources);
            At(style, "<Style TargetType=\"ui:TitleBarButton\" BasedOn=\"{StaticResource {x:Type ui:TitleBarButton}}\">");
            string Setter(string property)
            {
                var m = Regex.Match(style, "<Setter Property=\"" + property + "\" Value=\"([^\"]*)\"/>");
                Assert.True(m.Success, "the window buttons' style sets no " + property);
                return m.Groups[1].Value;
            }
            string width = Setter("Width"), height = Setter("Height"), margin = Setter("Margin");

            string button = Regex.Match(xaml, "<controls:StateToggleButton x:Name=\"FullScreenBtn\".*?>", RegexOptions.Singleline).Value;
            Assert.Contains("Width=\"" + width + "\" Height=\"" + height + "\"", button);
            Assert.Contains("VerticalAlignment=\"Top\" Margin=\"" + margin + "\"", button);

            // Centered in the bar: the space over a button equals the space under it.
            var bar = Regex.Match(xaml, "<ui:TitleBar x:Name=\"AppTitleBar\" Height=\"(\\d+)\"");
            Assert.True(bar.Success);
            double top = double.Parse(margin.Split(',')[1], System.Globalization.CultureInfo.InvariantCulture);
            double tall = double.Parse(height, System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(double.Parse(bar.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) - tall, top * 2);

            Assert.DoesNotContain("PaneToggleBtn", xaml);
        }

        /// <summary>The Welcome Tour, until its own step removes it, stops at
        /// the engine's card, the controller cards, the sidebar and the
        /// Extras item, and no longer at a status bar. Its strings for that
        /// stop are gone from every language.</summary>
        [Fact]
        public void TheTour_HasFourStops_AndNoneAtAStatusBar()
        {
            string code = WindowCode();
            int stops = At(code, "private (FrameworkElement Target, string Title, string Body)[] BuildTourStops() => new[]");
            int end = code.IndexOf("};", stops, StringComparison.Ordinal);
            string list = code.Substring(stops, end - stops);
            // One stop to a line, each line ending its tuple.
            Assert.Equal(4, Count(list, "_Body),"));
            At(list, "((FrameworkElement)HomePageView.EngineCard, Strings.Instance.Tour_Engine_Title, Strings.Instance.Tour_Engine_Body),");
            At(list, "(HomePageView.SlotsItemsControl, Strings.Instance.Tour_Cards_Title, Strings.Instance.Tour_Cards_Body),");
            At(list, "(NavView, Strings.Instance.Tour_Slots_Title, Strings.Instance.Tour_Slots_Body),");
            At(list, "(_navExtras, Strings.Instance.Extras_Title, Strings.Instance.Tour_Extras_Body),");
            Assert.DoesNotContain("Tour_Status", code);
            // The ring's lower bound is the window's own edge.
            At(code, "double bottom = Math.Min(origin.Y + h + 4, oh - 2);");
        }
    }
}
