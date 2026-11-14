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
                "<ItemsControl x:Name=\"SlotsItemsControl\"",
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
                // The first page's parts.
                "<StackPanel x:Name=\"FirstRunPanel\"",
                "AutomationProperties.AutomationId=\"Home_FirstRun_Device\"",
                "AutomationProperties.AutomationId=\"Home_FirstRun_Start\"",
                "AutomationProperties.AutomationId=\"Home_FirstRun_Types\"",
                "AutomationProperties.AutomationId=\"Home_FirstRun_MoreTypes\"",
                "AutomationProperties.AutomationId=\"Home_FirstRun_Pair\"",
                "AutomationProperties.AutomationId=\"Home_FirstRun_Profile\"",
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

        /// <summary>The engine's card, the page's first and its largest
        /// words: the flame in its ring is the switch it always was, and
        /// the worded button at the card's end raises the same click. The
        /// state is one large word, a polite live line, with a sentence
        /// under it and three numbers beside it, each with its label. While
        /// the engine runs the card and the ring stand in ember light, the
        /// ring's breathing, and Start is the page's one ember button.</summary>
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
            At(engine, "<TextBlock x:Name=\"EngineDevicesLine\" Text=\"{Binding HeroLine}\"");
            // The flame and the button say the same thing on hover, in each state.
            foreach (string tip in new[] { "Dashboard_EngineFlame_Stopped", "Dashboard_EngineFlame_Running", "Dashboard_EngineFlame_Idle", "Common_Stopping" })
                Assert.Equal(2, Count(engine, "Value=\"{Binding " + tip + ", Source={x:Static strings:Strings.Instance}}\""));

            // The card's own measures, the board's: a 76 ring with the
            // flame of a 38 icon, which is three quarters of the icon's
            // box, the word at 32 in the display face, numbers at 30.
            At(engine, "Width=\"76\" Height=\"76\"");
            At(engine, "BorderBrush=\"{TemplateBinding BorderBrush}\" BorderThickness=\"3\" CornerRadius=\"38\">");
            At(engine, "Width=\"29\" Height=\"29\" Stretch=\"Uniform\">");
            At(engine, "FontFamily=\"{StaticResource DisplayFontFamily}\" FontSize=\"32\" FontWeight=\"SemiBold\"");
            At(engine, "<controls:FlexLinePanel Margin=\"26,22,26,22\" Gap=\"28\" LineGap=\"18\">");
            At(engine, "<StackPanel controls:FlexLinePanel.Basis=\"250\" controls:FlexLinePanel.Grow=\"1\" VerticalAlignment=\"Center\">");

            // The light: one halo around the card and one around the ring,
            // which breathes, each lit only while the engine runs. The card's
            // is the card's own child, out to its outline by its margin.
            Assert.Equal(2, Count(engine, "<controls:Halo "));
            At(engine, "<controls:Halo x:Name=\"EngineHalo\" Margin=\"-1\" CornerRadius=\"12\" Radius=\"50\" Strength=\"0.07\" Rim=\"0.14\"");
            At(engine, "<controls:Halo x:Name=\"EngineRingHalo\" CornerRadius=\"38\" Radius=\"27.5\" Strength=\"0.30\" Breathes=\"True\"");
            Assert.Equal(2, Count(engine, "<Setter Property=\"IsLit\" Value=\"True\"/>"));
            Assert.Equal(1, Count(engine, "Breathes=\"True\""));
            At(engine, "<Border x:Name=\"EngineWash\" CornerRadius=\"11\" IsHitTestVisible=\"False\">");
            Assert.Matches(new Regex("<DataTrigger Binding=\"\\{Binding EngineStateKey\\}\" Value=\"Running\">\\s*"
                + "<Setter Property=\"BorderBrush\" Value=\"\\{DynamicResource EmberRingSoftBrush\\}\"/>"), engine);

            // Three numbers, each a text of its own with its label under it.
            int stats = At(engine, "<controls:FlexLinePanel x:Name=\"EngineStats\" Gap=\"36\" LineGap=\"14\" VerticalAlignment=\"Center\">");
            int button = At(engine, "<ui:Button x:Name=\"EngineButton\"");
            string numbers = engine.Substring(stats, button - stats);
            int rate = At(numbers, "<TextBlock x:Name=\"EngineRate\"");
            int live = At(numbers, "<TextBlock x:Name=\"EngineLive\"");
            int devices = At(numbers, "<TextBlock x:Name=\"EngineDevices\" Text=\"{Binding OnlineDevices, Mode=OneWay}\"");
            Assert.True(rate < live && live < devices);
            foreach (string label in new[] { "Home_Stat_PollingRate", "Home_Stat_ControllersLive", "Home_Stat_DevicesConnected" })
                At(numbers, "Text=\"{Binding " + label + ", Source={x:Static strings:Strings.Instance}}\"/>");
            Assert.Equal(3, Count(numbers, "<TextBlock Style=\"{StaticResource StatLabel}\""));
            // The rate's number is apart from its unit, in the language's
            // order, and a screen reader hears it whole. It is cold blue
            // while there is a rate, in every state but stopped.
            At(numbers, "AutomationProperties.Name=\"{Binding PollingFrequencyText, Mode=OneWay, TargetNullValue='', FallbackValue=''}\"");
            At(numbers, "<Run Text=\"{Binding PollingRateLead, Mode=OneWay}\"/><Run Text=\"{Binding PollingRateNumber, Mode=OneWay}\" FontSize=\"30\" FontWeight=\"SemiBold\"/><Run Text=\"{Binding PollingRateTail, Mode=OneWay}\"/>");
            Assert.DoesNotContain(" Hz", numbers);
            foreach (string state in new[] { "Running", "Idle", "Stopping" })
                At(numbers, "<DataTrigger Binding=\"{Binding EngineStateKey}\" Value=\"" + state + "\"><Setter Property=\"Foreground\" Value=\"{DynamicResource ColdBrush}\"/></DataTrigger>");
            // How many of the controllers are live, said whole to a screen reader.
            At(numbers, "AutomationProperties.Name=\"{Binding LiveLine, Mode=OneWay, TargetNullValue='', FallbackValue=''}\"");
            At(numbers, "<Run Text=\"{Binding LiveControllers, Mode=OneWay}\" FontSize=\"30\" FontWeight=\"SemiBold\" Foreground=\"{DynamicResource TextFillColorPrimaryBrush}\"/><Run Text=\" / \"/><Run Text=\"{Binding ControllerCount, Mode=OneWay}\"/>");

            // Start is ember, and only Start: the trigger is the stopped state's.
            string start = engine.Substring(button);
            Assert.Matches(new Regex("<DataTrigger Binding=\"\\{Binding EngineStateKey\\}\" Value=\"Stopped\">\\s*"
                + "<Setter Property=\"ToolTip\" Value=\"[^\"]*\"/>\\s*"
                + "<Setter Property=\"Background\" Value=\"\\{DynamicResource EmberBrush\\}\"/>"), start);
            Assert.Equal(1, Count(start, "<Setter Property=\"Background\" Value=\"{DynamicResource EmberBrush}\"/>"));
            At(start, "MinHeight=\"42\" MinWidth=\"96\" Padding=\"22,0\"");
        }

        /// <summary>What Home's engine card and the sidebar's foot read beside
        /// the state: a sentence for each state with no number in it, the
        /// count of the controllers that are forging, and the polling rate
        /// with its number apart from its unit.</summary>
        [Fact]
        public void TheEnginesLineItsLiveCountAndItsRate_FollowTheEngineAndTheControllers()
        {
            var vm = new DashboardViewModel();
            var changed = new List<string>();
            vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            Assert.Equal(Strings.Instance.Home_Hero_Stopped, vm.HeroLine);

            vm.EngineStateKey = "Running";
            Assert.Contains(nameof(DashboardViewModel.HeroLine), changed);
            Assert.Equal(Strings.Instance.Home_Hero_Forging, vm.HeroLine);
            vm.EngineStateKey = "Idle";
            Assert.Equal(Strings.Instance.Home_Hero_Idle, vm.HeroLine);
            vm.EngineStateKey = "Stopping";
            Assert.Equal(Strings.Instance.Common_Stopping, vm.HeroLine);
            // A sentence with a number in it would need plural forms.
            foreach (string line in new[] { Strings.Instance.Home_Hero_Forging, Strings.Instance.Home_Hero_Idle, Strings.Instance.Home_Hero_Stopped })
                Assert.DoesNotMatch(@"[\d{]", line);

            // The count follows the list and each controller's heat.
            vm.EngineStateKey = "Running";
            changed.Clear();
            var one = new SlotSummary(0);
            var two = new SlotSummary(1);
            vm.SlotSummaries.Add(one);
            vm.SlotSummaries.Add(two);
            Assert.Contains(nameof(DashboardViewModel.LiveLine), changed);
            Assert.Equal(0, vm.LiveControllers);
            Assert.Equal(2, vm.ControllerCount);
            Assert.Equal(SlotSummary.HeatCold, one.Heat);

            // On with a device and no virtual controller yet: waiting.
            changed.Clear();
            one.MappedDeviceCount = 1;
            Assert.Equal(SlotSummary.HeatWaiting, one.Heat);
            Assert.Equal(0, vm.LiveControllers);
            one.IsVirtualControllerConnected = true;
            Assert.Equal(SlotSummary.HeatForging, one.Heat);
            Assert.Contains(nameof(DashboardViewModel.LiveLine), changed);
            Assert.Equal(1, vm.LiveControllers);
            Assert.Equal(string.Format(Strings.Instance.Shell_Foot_Live_Format, 1, 2), vm.LiveLine);

            // The engine stops: a controller that would run waits.
            vm.EngineStateKey = "Stopped";
            Assert.Equal(SlotSummary.HeatWaiting, one.Heat);
            Assert.Equal(0, vm.LiveControllers);
            // One made while the engine is stopped is told so.
            var three = new SlotSummary(2) { MappedDeviceCount = 1, IsVirtualControllerConnected = true };
            vm.SlotSummaries.Add(three);
            Assert.Equal(SlotSummary.HeatWaiting, three.Heat);
            vm.EngineStateKey = "Running";
            Assert.Equal(SlotSummary.HeatForging, three.Heat);
            Assert.Equal(2, vm.LiveControllers);
            // Being made counts as forging, and off is cold whatever else.
            two.MappedDeviceCount = 1;
            two.IsInitializing = true;
            Assert.Equal(SlotSummary.HeatForging, two.Heat);
            one.IsEnabled = false;
            Assert.Equal(SlotSummary.HeatCold, one.Heat);
            Assert.Equal(2, vm.LiveControllers);
            // A controller that left the list is no longer counted or heard.
            vm.SlotSummaries.Remove(three);
            Assert.Equal(1, vm.LiveControllers);
            changed.Clear();
            three.IsEnabled = false;
            Assert.DoesNotContain(nameof(DashboardViewModel.LiveLine), changed);

            // The rate: the dash alone with no rate, then the number apart from its unit.
            changed.Clear();
            Assert.Equal(Strings.Instance.Dashboard_PollingDash, vm.PollingRateNumber);
            Assert.Equal(string.Empty, vm.PollingRateLead + vm.PollingRateTail);
            vm.PollingFrequency = 1000.4;
            Assert.Contains(nameof(DashboardViewModel.PollingRateNumber), changed);
            Assert.Equal(vm.PollingFrequencyText, vm.PollingRateLead + vm.PollingRateNumber + vm.PollingRateTail);
            Assert.Equal(1000.4.ToString("F0", System.Globalization.CultureInfo.CurrentCulture), vm.PollingRateNumber);
            Assert.NotEqual(string.Empty, vm.PollingRateLead + vm.PollingRateTail);
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

        /// <summary>A controller's card, as its board draws it. Its head is
        /// the type tile, the type's name with which one of that type it is
        /// over the preset, the state pill and the delete button. Its body
        /// is the controller's picture on a stage and, beside it, the
        /// devices that drive it, what it waits for, the stage icons and
        /// the counts. The name is one of the seven type names, picked by
        /// the slot's output type.</summary>
        [Fact]
        public void AControllersCard_IsHeadedByItsTypeAndNumber_WithThePresetUnderIt()
        {
            string page = Home();
            int template = At(page, "<DataTemplate DataType=\"{x:Type vm:SlotSummary}\">");
            int templateEnd = page.IndexOf("</DataTemplate>", template, StringComparison.Ordinal);
            string card = page.Substring(template, templateEnd - template);
            int headAt = At(card, "<!-- The head: [type tile] name #N over the");
            int bodyAt = At(card, "<!-- The body: the controller's picture on its");
            Assert.True(headAt < bodyAt);
            string head = card.Substring(headAt, bodyAt - headAt);
            int chip = At(head, "Click=\"TypeChip_Click\" Tag=\"{Binding PadIndex}\"");
            int name = At(head, "<Setter Property=\"Text\" Value=\"{Binding ControllerType_Xbox, Source={x:Static strings:Strings.Instance}}\"/>");
            int number = At(head, "<TextBlock Text=\"{Binding TypeInstanceLabel, StringFormat={}#{0}}\"");
            int preset = At(head, "<controls:MarqueeLabel Text=\"{Binding PresetName}\"");
            int pill = At(head, "IsOn=\"{Binding IsEnabled}\" Click=\"PowerToggle_Click\" Tag=\"{Binding PadIndex}\">");
            int delete = At(head, "Click=\"DeleteSlot_Click\" Tag=\"{Binding PadIndex}\">");
            Assert.True(chip < name && name < number && number < preset && preset < pill && pill < delete, "the card's head is out of order");
            Assert.Equal(4, Count(head.Substring(0, head.IndexOf("</Grid.ColumnDefinitions>", StringComparison.Ordinal)), "<ColumnDefinition "));
            At(head, "<Grid Margin=\"18,16,12,0\">");
            // The tile is 34 with the type's symbol at 20, the name 18 in
            // the display face.
            At(head, "Width=\"34\" Height=\"34\" VerticalAlignment=\"Center\"");
            At(head, "<Path FlowDirection=\"LeftToRight\" Width=\"20\" Height=\"20\" Stretch=\"Uniform\"");
            At(head, "<TextBlock FontFamily=\"{StaticResource DisplayFontFamily}\" FontSize=\"18\" FontWeight=\"SemiBold\"");
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

            // The state pill is the switch: a toggle named Enabled whose
            // words are the state, said again when the state changes.
            At(head, "<controls:StateToggleButton Grid.Column=\"2\" Style=\"{StaticResource StatePill}\"");
            At(head, "AutomationProperties.Name=\"{Binding Common_Enabled, Source={x:Static strings:Strings.Instance}}\"");
            At(head, "AutomationProperties.HelpText=\"{Binding StateWord, Mode=OneWay, TargetNullValue='', FallbackValue=''}\"");
            At(head, "ToolTip=\"{Binding Home_Card_Power, Source={x:Static strings:Strings.Instance}}\"");
            At(head, "<TextBlock common:LiveText.Polite=\"True\" Text=\"{Binding StateWord}\" Margin=\"7,0,0,0\"");
            // Delete is always there, in the head's last column, and one click.
            At(head, "<ui:Button Grid.Column=\"3\" Style=\"{StaticResource CardCloseButton}\" Margin=\"4,0,0,0\"");
            At(head, "AutomationProperties.Name=\"{Binding Main_DeleteVC, Source={x:Static strings:Strings.Instance}}\"");
            Assert.Equal(1, Count(page, "DeleteSlot_Click"));
            Assert.DoesNotContain("Property=\"Opacity\" Value=\"0\"", head);

            // The card says its heat in its edge and its light, and tells
            // what is inside it whether it is live.
            string edge = card.Substring(0, headAt);
            At(edge, "<Border CornerRadius=\"12\" BorderThickness=\"1\"");
            At(edge, "Tag=\"{Binding PadIndex}\"");
            At(edge, "Loaded=\"SlotCard_Loaded\"");
            Assert.Matches(new Regex("<DataTrigger Binding=\"\\{Binding Heat\\}\" Value=\"Forging\">\\s*"
                + "<Setter Property=\"BorderBrush\" Value=\"\\{DynamicResource EmberRingBrush\\}\"/>\\s*"
                + "<Setter Property=\"controls:Heat.IsLive\" Value=\"True\"/>"), edge);
            Assert.Matches(new Regex("<DataTrigger Binding=\"\\{Binding Heat\\}\" Value=\"Waiting\">\\s*"
                + "<Setter Property=\"BorderBrush\" Value=\"\\{DynamicResource WaitRingBrush\\}\"/>"), edge);
            At(edge, "<Setter Property=\"controls:Heat.IsLive\" Value=\"False\"/>");
            // The light is the card's own child, so a dragged card takes it
            // along and a hidden one hides it. Its margin takes it out to
            // the card's outline.
            At(edge, "<controls:Halo Grid.RowSpan=\"2\" Margin=\"-1\" CornerRadius=\"12\" Radius=\"42.5\" Strength=\"0.085\" Rim=\"0.18\"");
            Assert.Matches(new Regex("<DataTrigger Binding=\"\\{Binding Heat\\}\" Value=\"Forging\">\\s*<Setter Property=\"IsLit\" Value=\"True\"/>"), edge);
            Assert.DoesNotContain("Property=\"Opacity\" Value=\"0.72\"", card);

            // The body: the stage, then the devices.
            string under = card.Substring(bodyAt);
            At(under, "<controls:FlexLinePanel Grid.Row=\"1\" Margin=\"18,14,18,16\" Gap=\"18\" LineGap=\"14\">");
            int stage = At(under, "<controls:ClickBorder controls:FlexLinePanel.Basis=\"204\" Height=\"140\" CornerRadius=\"10\" BorderThickness=\"1\"");
            int driven = At(under, "<Grid controls:FlexLinePanel.Basis=\"220\" controls:FlexLinePanel.Grow=\"1\">");
            Assert.True(stage < driven);
            string picture = under.Substring(stage, driven - stage);
            // The stage opens the page for the keyboard and a screen reader.
            At(picture, "Click=\"OpenSlot_Click\" Tag=\"{Binding PadIndex}\"");
            At(picture, "AutomationProperties.Name=\"{Binding Dashboard_SlotCard_Configure, Source={x:Static strings:Strings.Instance}}\"");
            At(picture, "AutomationProperties.AutomationId=\"Dash_SlotStage\">");
            // Gray under, the colors over, by the heat.
            int gray = At(picture, "<controls:DecorativeImage Source=\"{Binding PictureGray}\" Stretch=\"Uniform\"");
            int color = At(picture, "<controls:DecorativeImage Source=\"{Binding Picture}\" Stretch=\"Uniform\"");
            Assert.True(gray < color);
            At(picture, "Visibility=\"{Binding HasPicture, Converter={StaticResource BoolToVisibilityConverter}, ConverterParameter=Invert}\">");
            Assert.Equal(4, Count(picture, "<DataTrigger Binding=\"{Binding Heat}\" Value=\"Waiting\">"));
            Assert.Equal(4, Count(picture, "<DataTrigger Binding=\"{Binding Heat}\" Value=\"Forging\">"));

            string devices = under.Substring(driven);
            At(devices, "Text=\"{Binding Pad_DrivenBy, Source={x:Static strings:Strings.Instance}}\"/>");
            At(devices, "<ItemsControl Grid.Row=\"1\" ItemsSource=\"{Binding MappedDevices}\" Focusable=\"False\"");
            At(devices, "ItemTemplate=\"{StaticResource DrivenByRow}\"/>");
            // With none mapped: the words and Add Device.
            At(devices, "Visibility=\"{Binding HasMappedDevices, Converter={StaticResource BoolToVisibilityConverter}, ConverterParameter=Invert}\">");
            At(devices, "Text=\"{Binding Mapping_NoDeviceMapped, Source={x:Static strings:Strings.Instance}}\"/>");
            At(devices, "Click=\"AddDevice_Click\" Tag=\"{Binding PadIndex}\"");
            At(devices, "Text=\"{Binding Pad_AddDevice, Source={x:Static strings:Strings.Instance}}\"/>");
            // What a waiting card waits for, said when it changes.
            At(devices, "<TextBlock Grid.Row=\"3\" common:LiveText.Polite=\"True\" Text=\"{Binding WaitHint}\"");
            At(devices, "Visibility=\"{Binding WaitHint, Converter={StaticResource StringToVisibility}}\"/>");
            // The foot: the stage icons, drawn by the template the
            // controller page's header uses, and the counts at the end.
            At(devices, "<controls:FlexLinePanel Grid.Row=\"5\" Margin=\"0,8,0,0\" Gap=\"6\" LineGap=\"4\" EndsApart=\"True\">");
            At(devices, "<ItemsControl ItemsSource=\"{Binding StageLedger}\" Focusable=\"False\" VerticalAlignment=\"Center\"");
            At(devices, "ItemTemplate=\"{StaticResource StageChipTemplate}\"");
            At(devices, "<TextBlock controls:FlexLinePanel.AtEnd=\"True\" FontSize=\"12\" VerticalAlignment=\"Center\"");
            foreach (string run in new[]
            {
                "<Run Text=\"{Binding MappedDeviceCount, Mode=OneWay}\"/>",
                "<Run Text=\"{Binding Dashboard_Mapped, Source={x:Static strings:Strings.Instance}, Mode=OneWay}\"/>",
                "<Run Text=\"{Binding ConnectedDeviceCount, Mode=OneWay}\"/>",
                "<Run Text=\"{Binding Dashboard_Connected, Source={x:Static strings:Strings.Instance}, Mode=OneWay}\"/>",
            })
                At(devices, run);

            // The cards stand two across at the board's width and share
            // the grid with the Add Controller tile.
            At(page, "<controls:TileGridPanel MinItemWidth=\"470\" Gap=\"16\" FillRows=\"True\"/>");
            At(page, "<controls:ClickBorder x:Name=\"AddControllerCard\"\r\n                                    MinHeight=\"132\" CornerRadius=\"12\"");
            // The heading says how many there are.
            At(page, "<TextBlock x:Name=\"ControllersHeading\" Style=\"{StaticResource PageSectionTitle}\"");
            At(page, "<TextBlock x:Name=\"ControllersCount\" Text=\"{Binding ControllerCount, Mode=OneWay}\"");
        }

        /// <summary>One device's row on a controller's card: a light that
        /// is ember while the device sends input, its name, a Bluetooth
        /// mark, the battery as a meter and the percentage, which says low
        /// or charging to a screen reader. A device that is not connected
        /// says so and shows neither link nor battery.</summary>
        [Fact]
        public void ADevicesRow_ShowsItsLightItsLinkAndItsBatteryAsAMeter()
        {
            string page = Home();
            int rowAt = At(page, "<DataTemplate x:Key=\"DrivenByRow\">");
            string row = page.Substring(rowAt, page.IndexOf("</DataTemplate>", rowAt, StringComparison.Ordinal) - rowAt);
            At(row, "<controls:Halo CornerRadius=\"3\" Radius=\"10\" Strength=\"0.45\" IsLit=\"{Binding IsSending}\"");
            At(row, "<Ellipse Width=\"6\" Height=\"6\" StrokeThickness=\"1\">");
            Assert.Matches(new Regex("<DataTrigger Binding=\"\\{Binding IsSending\\}\" Value=\"True\">\\s*"
                + "<Setter Property=\"Fill\" Value=\"\\{DynamicResource EmberBrush\\}\"/>"), row);
            Assert.Matches(new Regex("<DataTrigger Binding=\"\\{Binding IsSending\\}\" Value=\"True\">\\s*"
                + "<Setter Property=\"ToolTip\" Value=\"\\{Binding Home_Device_Sending, Source=\\{x:Static strings:Strings.Instance\\}\\}\"/>"), row);
            // The clear box the pointer finds is ten wider than the light,
            // and its margins give the ten back.
            At(row, "<Border Width=\"16\" Margin=\"-5,0,-5,0\" Background=\"Transparent\">");
            At(row, "<TextBlock Grid.Column=\"1\" Text=\"{Binding Name}\" Margin=\"6,0,6,0\" FontSize=\"13\"");
            int link = At(row, "<controls:DecorativeStrokeIcon Data=\"{StaticResource BluetoothLineGeometry}\" Size=\"13\" StrokeWidth=\"2\"");
            int meter = At(row, "<controls:BatteryMeter Margin=\"6,0,0,0\" Percent=\"{Binding BatteryPercent}\"");
            int percent = At(row, "<TextBlock Text=\"{Binding BatteryText}\" MinWidth=\"34\" Margin=\"6,0,0,0\" TextAlignment=\"Right\"");
            Assert.True(link < meter && meter < percent);
            At(row, "Charging=\"{Binding BatteryCharging}\" Low=\"{Binding BatteryLow}\"/>");
            At(row, "Visibility=\"{Binding IsBluetooth, Converter={StaticResource BoolToVisibilityConverter}}\"/>");
            At(row, "AutomationProperties.Name=\"{Binding BatterySpoken, Mode=OneWay, TargetNullValue='', FallbackValue=''}\"");
            Assert.Matches(new Regex("<DataTrigger Binding=\"\\{Binding BatteryLow\\}\" Value=\"True\">\\s*"
                + "<Setter Property=\"Foreground\" Value=\"\\{DynamicResource WaitBrush\\}\"/>"), row);
            // Connected and not connected are the two faces of one fact.
            At(row, "Visibility=\"{Binding IsOnline, Converter={StaticResource BoolToVisibilityConverter}}\">");
            At(row, "Text=\"{Binding Home_Device_NotConnected, Source={x:Static strings:Strings.Instance}}\"");
            At(row, "Visibility=\"{Binding IsOnline, Converter={StaticResource BoolToVisibilityConverter}, ConverterParameter=Invert}\"/>");
            Assert.DoesNotContain("Segoe MDL2 Assets", row);

            // The rune is the Bluetooth mark, one line.
            At(ExtrasPageTests.RepoText("PadForge.App", "Resources", "Shell.xaml"),
                "<Geometry x:Key=\"BluetoothLineGeometry\">M7 7 L17 17 L12 22 V2 L17 7 L7 17</Geometry>");
        }

        /// <summary>The words on a card's pill and its waiting line follow
        /// the controller's state, in the order the old flame's tooltip
        /// had, and a card reads them again when the language changes.</summary>
        [Fact]
        public void ACardsStateWordAndWaitingLine_FollowItsState()
        {
            var s = Strings.Instance;
            var slot = new SlotSummary(0) { EngineStopped = false };
            var changed = new List<string>();
            slot.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            Assert.Equal(s.Dashboard_StatusCold, slot.StateWord);
            Assert.Equal(string.Empty, slot.WaitHint);

            // One device mapped and none connected: it waits for that device.
            slot.MappedDeviceCount = 1;
            Assert.Contains(nameof(SlotSummary.StateWord), changed);
            Assert.Contains(nameof(SlotSummary.WaitHint), changed);
            Assert.Equal(SlotSummary.HeatWaiting, slot.Heat);
            Assert.Equal(s.Main_AwaitingDevices, slot.StateWord);
            Assert.Equal(s.Home_Card_StartsWhenThis, slot.WaitHint);
            slot.MappedDeviceCount = 3;
            Assert.Equal(s.Home_Card_StartsWhenOne, slot.WaitHint);
            // A device is there and the controller is not made yet: no line.
            slot.ConnectedDeviceCount = 1;
            Assert.Equal(string.Empty, slot.WaitHint);
            slot.IsInitializing = true;
            Assert.Equal(s.Main_Initializing, slot.StateWord);
            Assert.Equal(SlotSummary.HeatForging, slot.Heat);
            slot.IsInitializing = false;
            slot.IsVirtualControllerConnected = true;
            Assert.Equal(s.Main_Active, slot.StateWord);
            Assert.Equal(string.Empty, slot.WaitHint);
            // Its devices leave and it lives on through the grace: still forging.
            slot.ConnectedDeviceCount = 0;
            Assert.Equal(s.Main_Active, slot.StateWord);
            Assert.Equal(string.Empty, slot.WaitHint);

            // The engine stops: it waits for the engine, whatever is connected.
            changed.Clear();
            slot.EngineStopped = true;
            Assert.Contains(nameof(SlotSummary.StateWord), changed);
            Assert.Equal(s.Main_EngineStopped, slot.StateWord);
            Assert.Equal(s.Home_Card_StartsWithEngine, slot.WaitHint);
            slot.EngineStopped = false;

            // A failed make outranks waiting, and says nothing to wait for.
            slot.IsVirtualControllerConnected = false;
            slot.IsCreateFailed = true;
            Assert.Equal(s.Main_VcFailed, slot.StateWord);
            Assert.Equal(string.Empty, slot.WaitHint);
            slot.IsCreateFailed = false;
            Assert.Equal(s.Home_Card_StartsWhenOne, slot.WaitHint);

            // Off: Disabled, and nothing to wait for.
            slot.IsEnabled = false;
            Assert.Equal(s.Common_Disabled, slot.StateWord);
            Assert.Equal(string.Empty, slot.WaitHint);
            Assert.Equal(SlotSummary.HeatCold, slot.Heat);

            // No sentence has a number in it, so none needs plural forms.
            foreach (string line in new[] { s.Home_Card_StartsWithEngine, s.Home_Card_StartsWhenThis, s.Home_Card_StartsWhenOne })
                Assert.DoesNotMatch(@"[\d{]", line);

            // A change of language has every card read its words again.
            var dash = new DashboardViewModel();
            dash.SlotSummaries.Add(slot);
            changed.Clear();
            typeof(DashboardViewModel).GetMethod("OnCultureChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .Invoke(dash, null);
            Assert.Contains(nameof(SlotSummary.StateWord), changed);
            Assert.Contains(nameof(SlotSummary.WaitHint), changed);
        }

        /// <summary>A mapped device's battery facts come from one reading:
        /// the level, whether it charges, and whether it is low, which is
        /// at or under the level Settings names while it does not charge. A
        /// screen reader hears which. Its Bluetooth mark is its transport
        /// glyph's.</summary>
        [Fact]
        public void AMappedDevicesBattery_IsOneReading_AndSaysLowOrCharging()
        {
            var s = Strings.Instance;
            var device = new PadViewModel.MappedDeviceInfo();
            var changed = new List<string>();
            device.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            Assert.Equal(-1, device.BatteryPercent);
            Assert.False(device.HasBattery);
            Assert.Equal(string.Empty, device.BatterySpoken);

            device.SetBattery(90, false, 15);
            Assert.Equal(90, device.BatteryPercent);
            Assert.Equal("90%", device.BatteryText);
            Assert.True(device.HasBattery);
            Assert.False(device.BatteryLow);
            Assert.Equal(string.Format(s.Home_FirstRun_Battery_Format, "90%"), device.BatterySpoken);
            Assert.Contains(nameof(PadViewModel.MappedDeviceInfo.BatteryPercent), changed);
            Assert.Contains(nameof(PadViewModel.MappedDeviceInfo.BatterySpoken), changed);

            // At the level and under it: low. One over: not.
            device.SetBattery(15, false, 15);
            Assert.True(device.BatteryLow);
            Assert.Equal(string.Format(s.Home_Battery_Low_Format, "15%"), device.BatterySpoken);
            device.SetBattery(16, false, 15);
            Assert.False(device.BatteryLow);
            device.SetBattery(3, false, 15);
            Assert.True(device.BatteryLow);
            // Charging is never low.
            device.SetBattery(3, true, 15);
            Assert.False(device.BatteryLow);
            Assert.True(device.BatteryCharging);
            Assert.Equal(string.Format(s.Home_Battery_Charging_Format, "3%"), device.BatterySpoken);
            // The same reading again raises nothing.
            changed.Clear();
            device.SetBattery(3, true, 15);
            Assert.Empty(changed);
            // No battery: nothing, and never charging.
            device.SetBattery(-1, true, 15);
            Assert.Equal(-1, device.BatteryPercent);
            Assert.False(device.BatteryCharging);
            Assert.False(device.HasBattery);
            Assert.Equal(string.Empty, device.BatteryText);
            Assert.Equal(string.Empty, device.BatterySpoken);
            foreach (string format in new[] { s.Home_Battery_Low_Format, s.Home_Battery_Charging_Format })
                Assert.Contains("{0}", format);

            changed.Clear();
            Assert.False(device.IsBluetooth);
            device.TransportGlyph = "\uE702";
            Assert.True(device.IsBluetooth);
            Assert.Contains(nameof(PadViewModel.MappedDeviceInfo.IsBluetooth), changed);
            device.IsSending = true;
            Assert.Contains(nameof(PadViewModel.MappedDeviceInfo.IsSending), changed);

            // The service sets them from the same reading the Devices rows
            // get, with the alert's own level.
            string svc = ExtrasPageTests.RepoText("PadForge.App", "Services", "InputService.cs");
            At(svc, "int lowAt = _mainVm.Settings?.BatteryNotifyThreshold ?? 15;");
            At(svc, "dev.SetBattery(pct, charging, lowAt);");
        }

        // ── the extras in use ──────────────────────────────────────────        // ── the extras in use ──────────────────────────────────────────

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
            At(page, "<controls:TileGridPanel x:Name=\"ExtrasInUseRows\" MinItemWidth=\"300\" Gap=\"16\" FillRows=\"True\"");
            At(page, "controls:ExtraTile.OpenRequested=\"ExtraInUse_OpenRequested\">");
            // The tiles are lit while the engine is on, and steel while it
            // is stopped: the row tells them, and the style draws it.
            At(page, "controls:Heat.IsLive=\"{Binding IsEngineOn}\"");
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
            // Lit: a warm square and a faint ember edge. Not live: steel.
            At(home, "<Border x:Name=\"Card\" Style=\"{StaticResource CardBorder}\" Padding=\"0\" Margin=\"0\"");
            At(home, "BorderBrush=\"{DynamicResource EmberRingFaintBrush}\">");
            At(home, "<Border x:Name=\"IconSquare\" Width=\"40\" Height=\"40\" CornerRadius=\"10\" VerticalAlignment=\"Center\"");
            Assert.Matches(new Regex("<Trigger Property=\"controls:Heat.IsLive\" Value=\"False\">\\s*"
                + "<Setter TargetName=\"Card\" Property=\"BorderBrush\" Value=\"\\{DynamicResource CardStrokeColorDefaultBrush\\}\"/>\\s*"
                + "<Setter TargetName=\"IconSquare\" Property=\"Background\" Value=\"\\{DynamicResource SteelRaisedBrush\\}\"/>\\s*"
                + "<Setter TargetName=\"Icon\" Property=\"Foreground\" Value=\"\\{DynamicResource TextFillColorTertiaryBrush\\}\"/>"), home);
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
            At(head, "<Style TargetType=\"TextBlock\" BasedOn=\"{StaticResource PageSectionTitle}\">");
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

        /// <summary>The Extras page's count, 2 of 10 on, is the number Home
        /// counts, in three parts: what the language puts before the
        /// number, the number, and what it puts after, with the total in
        /// its place. A screen reader has the sentence whole. A switch
        /// that changes the count tells all four.</summary>
        [Fact]
        public void TheExtrasPagesCount_IsHomesNumber_InThreeParts()
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
                vm.OverlaysAnyEnabled = true;
                Assert.Equal(2, vm.ExtrasInUseCount);

                // The English, read from the file, since another test may
                // have the interface in another language as this one runs.
                Assert.Contains("<data name=\"Extras_OnCount_Format\" xml:space=\"preserve\"><value>{0} of {1} on</value></data>",
                    ExtrasPageTests.RepoText("PadForge.App", "Resources", "Strings", "Strings.resx"));
                string format = Strings.Instance.Extras_OnCount_Format;
                int at = format.IndexOf("{0}", StringComparison.Ordinal);
                Assert.True(at >= 0 && format.Contains("{1}"), "the count's string lost a place: " + format);
                Assert.Equal("2", vm.ExtrasOnNumber);
                Assert.Equal(format.Substring(0, at).Replace("{1}", "10"), vm.ExtrasOnLead);
                Assert.Equal(format.Substring(at + 3).Replace("{1}", "10"), vm.ExtrasOnTail);
                Assert.Equal(string.Format(format, 2, 10), vm.ExtrasOnSpoken);
                Assert.Equal(vm.ExtrasOnSpoken, vm.ExtrasOnLead + vm.ExtrasOnNumber + vm.ExtrasOnTail);

                var changed = new List<string>();
                vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
                vm.EnableTwitchChat = true;
                foreach (string part in new[] { nameof(vm.ExtrasOnLead), nameof(vm.ExtrasOnNumber), nameof(vm.ExtrasOnTail), nameof(vm.ExtrasOnSpoken) })
                    Assert.Contains(part, changed);
                Assert.Equal("3", vm.ExtrasOnNumber);
                Assert.Equal(string.Format(format, 3, 10), vm.ExtrasOnSpoken);
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
                    // A twin is owed the offer when it has a product identity
                    // to be remembered by.
                    string text = File.ReadAllText(file);
                    int n = Count(text, "HomeOfferPending = true") + Count(text, "HomeOfferPending = productGuid != Guid.Empty");
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
            // Use It opens Add a Controller for that device, from Home's
            // offer and from the Devices page's Use It and New Controller.
            Assert.Matches(@"HomePageView\.HomeOfferUseRequested \+= \(s, args\) => OpenAddController\(args\.Device, args\.Anchor\);", code);
            Assert.Matches(@"DevicesPageView\.NewControllerRequested \+= \(s, args\) => OpenAddController\(args\.Device, args\.Anchor\);", code);
            Assert.Equal(2, Count(code, "OpenAddController(args.Device, args.Anchor);"));
            // One method makes a controller, for every tile of the dialog and
            // of Home's first page: the dialog closes, the new slot takes the
            // device, the offer is read again, and the slot's page opens.
            int maker = At(code, "private void CreateController(VirtualControllerType type, Guid device)");
            int makerEnd = code.IndexOf("\n        }", maker, StringComparison.Ordinal);
            string method = code.Substring(maker, makerEnd - maker);
            int close = At(method, "CloseAddController(returnFocus: false, restoreNav: true);");
            int create = At(method, "int newSlot = _deviceService.CreateSlot(type);");
            int assign = At(method, "_deviceService.AssignDeviceToSlot(device, newSlot);");
            int reread = At(method, "_inputService.ReEvaluateHomeOffer();");
            int open = At(method, "NavigateToSlot(");
            Assert.True(close < create && create < assign && assign < reread && reread < open);
            foreach (string gone in new[] { "ShowControllerTypePopup", "CreateFromBar", "deviceForNewSlot" })
                Assert.DoesNotContain(gone, code);
            // The switch is followed at once.
            Assert.Matches(new Regex("if \\(e\\.PropertyName == nameof\\(SettingsViewModel\\.AssignOfferHome\\)\\)\\s*"
                + "_inputService\\?\\.ReEvaluateHomeOffer\\(\\);"), code);

            string page = Home();
            At(page, "Visibility=\"{Binding HasHomeOffer, Converter={StaticResource BoolToVisibilityConverter}}\"");
            At(page, "<TextBlock x:Name=\"HomeOfferLine\" common:LiveText.Polite=\"True\"");
            // The sentence wraps, and the two buttons go under it together.
            At(page, "<Border x:Name=\"HomeOffer\" Margin=\"0,12,0,0\" Padding=\"18,12,14,12\" CornerRadius=\"12\"");
            At(page, "controls:FlexLinePanel.Basis=\"320\" controls:FlexLinePanel.Grow=\"1\"");
            int use = At(page, "<ui:Button x:Name=\"HomeOfferUse\"");
            int dismiss = At(page, "<ui:Button x:Name=\"HomeOfferDismiss\"");
            Assert.DoesNotContain("</StackPanel>", page.Substring(use, dismiss - use));
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
            At(xaml, "OpenPaneLength=\"264\"");
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
            // The controllers scroll in the room over the foot, and the
            // foot (Settings, About and the engine) stands at the pane's
            // lower edge: the scrolling part ends before the foot starts.
            // The foot comes second in the markup, so the keyboard reaches
            // it after the controllers. No hairline stands between them:
            // an entry there was an unnamed item to a screen reader.
            // The list runs the pane's whole width with its entries inset
            // inside it, so the light around a pill has the pane's inset to
            // fade in and is not cut at the pill's two ends.
            At(shell, "<Grid Margin=\"12,14,12,14\">");
            At(shell, "<ui:DynamicScrollViewer x:Name=\"PART_ScrollViewer\" Margin=\"-12,0,-12,0\"");
            At(shell, "<controls:NavItemsControl x:Name=\"PART_MenuItemsItemsControl\" Margin=\"12,0,12,0\"");
            Assert.Equal(12.0, PadForge.MainWindow.PillLightReach);
            At(code, "Radius = PillLightReach,");
            int scroll = At(shell, "<ui:DynamicScrollViewer x:Name=\"PART_ScrollViewer\"");
            int scrollEnd = At(shell, "</ui:DynamicScrollViewer>");
            int foot = At(shell, "<StackPanel Grid.Row=\"1\" Margin=\"0,12,0,0\">");
            Assert.True(scroll < menu && menu < scrollEnd && scrollEnd < foot && foot < footer);
            At(shell, "<RowDefinition Height=\"*\"/>\r\n                        <RowDefinition Height=\"Auto\"/>");
            Assert.DoesNotContain("<Border Height=\"1\"", shell);
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
            Assert.Equal(4, Count(xaml, "Grid.RowSpan=\"2\""));

            // The foot.
            int foot = At(xaml, "<ui:NavigationView.PaneFooter>");
            int footEnd = At(xaml, "</ui:NavigationView.PaneFooter>");
            string engine = xaml.Substring(foot, footEnd - foot);
            At(engine, "<Border x:Name=\"EngineFoot\"");
            At(engine, "<Path x:Name=\"EngineFootFlame\" FlowDirection=\"LeftToRight\" Data=\"{StaticResource FlameOuterGeometry}\"");
            At(engine, "<TextBlock x:Name=\"EngineFootState\" Text=\"{Binding EngineStatusText}\"");
            // Under the state: how many of the controllers are live.
            At(engine, "<TextBlock x:Name=\"EngineFootLine\" Text=\"{Binding Dashboard.LiveLine}\"");
            // The rate is the text Home's engine card prints, taken apart so
            // its number stands larger than its unit, in the order the
            // language's own format puts them. A screen reader hears it whole.
            At(engine, "<TextBlock x:Name=\"EngineFootRate\" Grid.Column=\"2\" VerticalAlignment=\"Center\"");
            At(engine, "AutomationProperties.Name=\"{Binding Dashboard.PollingFrequencyText, Mode=OneWay, TargetNullValue='', FallbackValue=''}\"");
            At(engine, "<Run Text=\"{Binding Dashboard.PollingRateLead, Mode=OneWay}\"/><Run Text=\"{Binding Dashboard.PollingRateNumber, Mode=OneWay}\" FontSize=\"17\" FontWeight=\"SemiBold\"/><Run Text=\"{Binding Dashboard.PollingRateTail, Mode=OneWay}\"/>");
            Assert.DoesNotContain(" Hz", engine);
            foreach (string state in new[] { "Running", "Idle", "Stopping" })
                Assert.Contains("<DataTrigger Binding=\"{Binding Dashboard.EngineStateKey}\" Value=\"" + state + "\">", engine);
            // The block is lit only while the engine runs: its two halos, its
            // edge, its wash and the rate's color each have the one trigger.
            Assert.Equal(2, Count(engine, "<controls:Halo "));
            // A board's shadow of blur 14 at 0.55 is a halo of 17.5 at
            // 0.275, and the block's of 22 at 0.12 one of 27.5 at 0.06.
            At(engine, "<controls:Halo CornerRadius=\"19\" Radius=\"17.5\" Strength=\"0.275\" Breathes=\"True\" VerticalAlignment=\"Center\">");
            At(engine, "<controls:Halo x:Name=\"EngineFootHalo\" Margin=\"0,12,0,0\" CornerRadius=\"12\" Radius=\"27.5\" Strength=\"0.06\">");
            Assert.Equal(1, Count(engine, "Breathes=\"True\""));
            Assert.Equal(2, Count(engine, "<Setter Property=\"IsLit\" Value=\"True\"/>"));
            // The foot reads the window's view model, handed to its outer
            // element by name.
            At(code, "EngineFootHalo.DataContext = _viewModel;");

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

        // ── the first page (#574) ──────────────────────────────────────

        /// <summary>Home shows its first page while no virtual controller
        /// exists, and the rest of Home as soon as one does.</summary>
        [Fact]
        public void Home_IsOnItsFirstPage_WhileNoControllerExists()
        {
            var vm = new DashboardViewModel();
            Assert.True(vm.IsFirstRun);
            var raised = new List<string>();
            vm.PropertyChanged += (s, e) => raised.Add(e.PropertyName);

            vm.RefreshActiveSlots(new[] { 0 }, true);
            Assert.False(vm.IsFirstRun);
            Assert.Equal(1, raised.Count(n => n == nameof(DashboardViewModel.IsFirstRun)));

            // A change of the set is decided once the list is whole: no turn
            // back to the first page in between.
            raised.Clear();
            vm.RefreshActiveSlots(new[] { 1, 2 }, true);
            Assert.DoesNotContain(nameof(DashboardViewModel.IsFirstRun), raised);

            vm.RefreshActiveSlots(Array.Empty<int>(), true);
            Assert.True(vm.IsFirstRun);
            Assert.Contains(nameof(DashboardViewModel.IsFirstRun), raised);
        }

        /// <summary>The card names the controller, says that none is
        /// connected, or says the engine is stopped, and the words under the
        /// heading follow it.</summary>
        [Fact]
        public void TheFirstCard_ShowsTheDevice_NoDevice_OrTheStoppedEngine()
        {
            var vm = new DashboardViewModel { EngineStateKey = "Idle" };
            var g = Guid.NewGuid();
            vm.SetFirstRunDevice(g, "DualSense Wireless Controller", null);
            Assert.True(vm.FirstRunShowsDevice);
            Assert.False(vm.FirstRunShowsNoDevice || vm.FirstRunShowsStopped);
            Assert.Equal("DualSense Wireless Controller", vm.FirstRunCardTitle);
            Assert.Equal(g, vm.FirstRunDeviceGuid);
            Assert.Equal(Strings.Instance.Home_FirstRun_Body, vm.FirstRunBody);
            Assert.False(vm.HasFirstRunLine);

            var raised = new List<string>();
            vm.PropertyChanged += (s, e) => raised.Add(e.PropertyName);
            vm.ClearFirstRunDevice();
            Assert.True(vm.FirstRunShowsNoDevice);
            Assert.False(vm.FirstRunShowsDevice);
            Assert.Equal(Strings.Instance.Home_FirstRun_NoDevice, vm.FirstRunCardTitle);
            Assert.Equal(Strings.Instance.Home_FirstRun_BodyNoDevice, vm.FirstRunBody);
            Assert.Contains(nameof(DashboardViewModel.FirstRunCardTitle), raised);

            raised.Clear();
            vm.EngineStateKey = "Stopped";
            Assert.True(vm.FirstRunShowsStopped);
            Assert.False(vm.FirstRunShowsDevice || vm.FirstRunShowsNoDevice);
            Assert.Equal(Strings.Instance.Main_EngineStopped, vm.FirstRunCardTitle);
            Assert.Equal(Strings.Instance.Home_FirstRun_BodyNoDevice, vm.FirstRunBody);
            Assert.Contains(nameof(DashboardViewModel.FirstRunShowsStopped), raised);
            Assert.Contains(nameof(DashboardViewModel.FirstRunCardTitle), raised);
        }

        /// <summary>The first page's walk names the first connected game
        /// controller that no virtual controller uses, and keeps the one shown
        /// while it qualifies, as the offer's walk does.</summary>
        [Fact]
        public void TheFirstPageWalk_NamesTheFirstUnusedController_AndKeepsItStanding()
        {
            var (vm, svc) = Arrange();
            var keyboard = Device("Some Keyboard", InputDeviceType.Keyboard);
            var away = Device("Away Pad", online: false);
            var used = Device("Used Pad");
            var first = Device("First Pad");
            var second = Device("Second Pad");
            lock (SettingsManager.UserSettings.SyncRoot)
                SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = used.InstanceGuid, MapTo = 0 });
            vm.Dashboard.EngineStateKey = "Idle";

            svc.EvaluateFirstRunDevice();
            Assert.Equal(first.InstanceGuid, vm.Dashboard.FirstRunDeviceGuid);
            Assert.Contains("First Pad", vm.Dashboard.FirstRunCardTitle);
            Assert.False(InputService.IsUnusedGameController(keyboard));

            // A device ahead of it in the registry comes back: the card does
            // not jump to it.
            away.IsOnline = true;
            svc.EvaluateFirstRunDevice();
            Assert.Equal(first.InstanceGuid, vm.Dashboard.FirstRunDeviceGuid);

            // It leaves: the next one in the registry is named.
            first.IsOnline = false;
            svc.EvaluateFirstRunDevice();
            Assert.Equal(away.InstanceGuid, vm.Dashboard.FirstRunDeviceGuid);

            // None left: the card names none.
            away.IsOnline = false;
            second.IsOnline = false;
            svc.EvaluateFirstRunDevice();
            Assert.False(vm.Dashboard.HasFirstRunDevice);

            // Between walks the window asks again, which does nothing while
            // the engine is stopped.
            second.IsOnline = true;
            svc.ReEvaluateFirstRunDevice();
            Assert.False(vm.Dashboard.HasFirstRunDevice);
            Assert.Empty(svc.UnusedGameControllers());
        }

        /// <summary>The card's line: Bluetooth and the battery, the battery
        /// following the row as it changes, and Bluetooth alone without a
        /// battery.</summary>
        [Fact]
        public void TheFirstPageLine_IsBluetoothAndTheBattery()
        {
            var vm = new DashboardViewModel { EngineStateKey = "Idle" };
            var row = new DeviceRowViewModel
            {
                DevicePath = @"\\?\HID#{00001124-0000-1000-8000-00805f9b34fb}_VID&0002054c_PID&0ce6#a&1&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}",
                VendorId = 0x054C,
                ProductId = 0x0CE6,
                BatteryPercent = 82,
            };
            Assert.True(row.IsBluetoothLink);
            vm.SetFirstRunDevice(Guid.NewGuid(), "DualSense Wireless Controller", row);
            Assert.True(vm.HasFirstRunLine);
            Assert.Equal("Bluetooth, battery ", vm.FirstRunLineLead);
            Assert.Equal("82%", vm.FirstRunLineBattery);
            Assert.Equal(string.Empty, vm.FirstRunLineTail);
            // As it is drawn: the battery's word is its picture, before
            // the percentage. The words stay whole for a screen reader.
            Assert.Equal("Bluetooth, ", vm.FirstRunLineFacts);
            Assert.Equal(PadForge.Common.BatteryGlyphs.For(82, false), vm.FirstRunLineGlyph);
            Assert.Equal(" ", vm.FirstRunLineGap);
            Assert.Equal("Bluetooth, battery 82%", vm.FirstRunLineText);

            var raised = new List<string>();
            vm.PropertyChanged += (s, e) => raised.Add(e.PropertyName);
            row.BatteryPercent = 50;
            Assert.Contains(nameof(DashboardViewModel.FirstRunLineBattery), raised);
            Assert.Contains(nameof(DashboardViewModel.FirstRunLineGlyph), raised);
            Assert.Equal("50%", vm.FirstRunLineBattery);
            Assert.Equal(PadForge.Common.BatteryGlyphs.For(50, false), vm.FirstRunLineGlyph);
            raised.Clear();
            row.BatteryCharging = true;
            Assert.Contains(nameof(DashboardViewModel.FirstRunLineGlyph), raised);
            Assert.Equal(PadForge.Common.BatteryGlyphs.For(50, true), vm.FirstRunLineGlyph);
            row.BatteryCharging = false;

            row.BatteryPercent = -1;
            Assert.Equal(Strings.Instance.Pad_Transport_Bluetooth, vm.FirstRunLineLead);
            Assert.Equal(Strings.Instance.Pad_Transport_Bluetooth, vm.FirstRunLineFacts);
            Assert.Equal(string.Empty, vm.FirstRunLineGlyph);
            Assert.Equal(string.Empty, vm.FirstRunLineGap);
            Assert.Equal(string.Empty, vm.FirstRunLineBattery);

            // A cleared card lets the row go.
            vm.ClearFirstRunDevice();
            raised.Clear();
            row.BatteryPercent = 40;
            Assert.DoesNotContain(nameof(DashboardViewModel.FirstRunLineBattery), raised);
        }

        /// <summary>The line under the tiles links the four other types.</summary>
        [Fact]
        public void TheMoreTypesLine_LinksTheFourOtherTypes()
        {
            var vm = new DashboardViewModel();
            string link = vm.MoreTypesLink;
            foreach (string type in new[] { Strings.Instance.ControllerType_Nintendo, Strings.Instance.ControllerType_Extended,
                         Strings.Instance.ControllerType_MIDI, Strings.Instance.ControllerType_VR })
                Assert.Contains(type, link);
            Assert.Equal("More types: ", vm.MoreTypesLead);
            Assert.Equal(string.Empty, vm.MoreTypesTail);
            Assert.Equal(new[] { "Home_FirstRun_Xbox", "Home_FirstRun_PlayStation", "Home_FirstRun_KeyboardMouse" },
                vm.FirstRunTypes.Select(t => t.AutomationId).ToArray());
        }

        /// <summary>The first page as its board draws it, top to bottom.</summary>
        [Fact]
        public void TheFirstPage_RunsInTheBoardsOrder()
        {
            string page = Home();
            int panel = At(page, "<StackPanel x:Name=\"FirstRunPanel\" MaxWidth=\"720\" Margin=\"36,56,36,40\"");
            int content = At(page, "<StackPanel x:Name=\"HomeContent\" Margin=\"36,26,36,36\"");
            Assert.True(panel < content, "the first page does not stand before the rest of Home");
            string first = page.Substring(panel, content - panel);
            int card = At(first, "<Border x:Name=\"FirstRunDeviceCard\" Style=\"{StaticResource CardBorder}\" Padding=\"18,14\"");
            int title = At(first, "Text=\"{Binding Home_FirstRun_Title, Source={x:Static strings:Strings.Instance}}\"");
            int body = At(first, "<TextBlock x:Name=\"FirstRunBody\" Text=\"{Binding FirstRunBody}\" Margin=\"0,8,0,0\" FontSize=\"15\"");
            int tiles = At(first, "<controls:TileGridPanel MinItemWidth=\"200\" Gap=\"16\" FillRows=\"True\"/>");
            int more = At(first, "<TextBlock x:Name=\"MoreTypesLine\"");
            int foot = At(first, "<controls:FlexLinePanel Gap=\"32\" LineGap=\"12\">");
            Assert.True(card < title && title < body && body < tiles && tiles < more && more < foot, "the first page is out of order");
            At(first, "FontSize=\"30\" FontWeight=\"SemiBold\"");
            At(first, "Click=\"FirstRunStart_Click\"");
            Assert.Equal(2, Count(page, "Click=\"EngineToggle_Click\""));
            At(page, "Visibility=\"{Binding IsFirstRun, Converter={StaticResource BoolToVisibilityConverter}, ConverterParameter=Invert}\"");
        }

        /// <summary>A tile of the first page makes the controller for the
        /// device the card names, and the walk that names it follows the
        /// offer's.</summary>
        [Fact]
        public void TheFirstPageTiles_MakeAControllerForTheDeviceShown()
        {
            string code = WindowCode();
            At(code, "HomePageView.FirstRunTypeChosen += (s, type) => CreateController(type, _viewModel.Dashboard.FirstRunDeviceGuid);");
            At(code, "HomePageView.FirstRunMoreTypesRequested += (s, opener) => OpenAddController(_viewModel.Dashboard.FirstRunDeviceGuid, opener);");
            string svc = ExtrasPageTests.RepoText("PadForge.App", "Services", "InputService.cs");
            int pads = svc.IndexOf("EvaluateAssignOffers();", StringComparison.Ordinal);
            int offer = svc.IndexOf("EvaluateHomeOffer();", pads, StringComparison.Ordinal);
            int named = svc.IndexOf("EvaluateFirstRunDevice();", offer, StringComparison.Ordinal);
            Assert.True(pads > 0 && offer > pads && named > offer && named - offer < 60,
                "the first page's walk does not follow the offer's");
            int clear = At(svc, "_mainVm.Dashboard.ClearHomeOffer();");
            int clearFirst = At(svc, "_mainVm.Dashboard.ClearFirstRunDevice();");
            Assert.True(clearFirst > clear && clearFirst - clear < 80, "a stop does not clear the first page's card beside the offer");
        }

        /// <summary>The Welcome Tour is gone (#574): its overlay, its code, its
        /// setting and its strings. A settings file that still has the
        /// setting loads with it skipped, and the next save leaves it out.
        /// The pre-v4 marker beside the exe is still deleted, and nothing
        /// reads it.</summary>
        [Fact]
        public void TheWelcomeTourIsGone_WithItsOverlayItsSettingAndItsStrings()
        {
            var files = new[]
            {
                Window(), WindowCode(),
                ExtrasPageTests.RepoText("PadForge.App", "ViewModels", "SettingsViewModel.cs"),
                ExtrasPageTests.RepoText("PadForge.App", "Services", "SettingsService.cs"),
                // The Settings page's Show Welcome Tour button went last.
                ExtrasPageTests.RepoText("PadForge.App", "Views", "SettingsPage.xaml"),
                ExtrasPageTests.RepoText("PadForge.App", "Views", "SettingsPage.xaml.cs"),
            };
            foreach (string text in files)
                foreach (string gone in new[] { "FirstRunOverlay", "TourCanvas", "WelcomePanel", "BuildTourStops", "MaybeShowFirstRun", "FirstRunTourCompleted", "StartFirstRunTour", "ShowTour" })
                    Assert.DoesNotContain(gone, text);

            string designer = ExtrasPageTests.RepoText("PadForge.App", "Resources", "Strings", "Strings.Designer.cs");
            string resx = ExtrasPageTests.RepoText("PadForge.App", "Resources", "Strings", "Strings.resx");
            foreach (string key in new[]
            {
                "FirstRun_Title", "FirstRun_Body", "FirstRun_Begin", "FirstRun_Skip", "FirstRun_Next", "FirstRun_Done",
                "Tour_Slots_Title", "Tour_Slots_Body", "Tour_Engine_Title", "Tour_Engine_Body",
                "Tour_Cards_Title", "Tour_Cards_Body", "Tour_Extras_Body", "Settings_ShowTour",
            })
            {
                Assert.DoesNotContain("\"" + key + "\"", designer);
                Assert.DoesNotContain("\"" + key + "\"", resx);
            }

            string code = WindowCode();
            At(code, "\"PadForge.firstrun\"");
            Assert.Contains("System.IO.File.Delete(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, \"PadForge.firstrun\"))", code);

            const string old = "<AppSettingsData><FirstRunTourCompleted>true</FirstRunTourCompleted><Use2DControllerView>true</Use2DControllerView></AppSettingsData>";
            var serializer = new XmlSerializer(typeof(AppSettingsData));
            AppSettingsData read;
            using (var reader = new StringReader(old))
                read = (AppSettingsData)serializer.Deserialize(reader);
            Assert.True(read.Use2DControllerView);
            var written = new StringWriter();
            serializer.Serialize(written, read);
            Assert.DoesNotContain("FirstRunTourCompleted", written.ToString());
            Assert.Contains("<Use2DControllerView>true</Use2DControllerView>", written.ToString());
        }
    }
}
