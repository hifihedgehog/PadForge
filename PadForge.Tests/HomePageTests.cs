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
                "<Border x:Name=\"EngineCard\"",
                "<ItemsControl x:Name=\"SlotsItemsControl\">",
                "<reset:ClickBorder x:Name=\"AddControllerCard\"",
                "<CollectionContainer x:Name=\"SlotsCollectionContainer\"/>",
            })
                At(page, kept);
            foreach (string added in new[]
            {
                "AutomationProperties.AutomationId=\"Home_EngineButton\"",
                "AutomationProperties.AutomationId=\"HomeOffer\"",
                "AutomationProperties.AutomationId=\"HomeOfferUse\"",
                "AutomationProperties.AutomationId=\"HomeOfferDismiss\"",
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
        /// state reads as a polite live line with the device count under
        /// it.</summary>
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
            At(engine, "Text=\"{Binding OnlineDevices, Mode=OneWay}\"");
            At(engine, "Text=\"{Binding TotalDevices, Mode=OneWay}\"");
            // The flame and the button say the same thing on hover, in each state.
            foreach (string tip in new[] { "Dashboard_EngineFlame_Stopped", "Dashboard_EngineFlame_Running", "Dashboard_EngineFlame_Idle", "Common_Stopping" })
                Assert.Equal(2, Count(engine, "Value=\"{Binding " + tip + ", Source={x:Static strings:Strings.Instance}}\""));
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

        /// <summary>A controller's card opens with its flame, its type chip,
        /// the type's name and which one of that type it is, where "Slot N"
        /// stood. The Preset has the row under it. The name is one of the
        /// seven type names, picked by the slot's output type.</summary>
        [Fact]
        public void AControllersCard_IsHeadedByItsTypeAndNumber_WithThePresetUnderIt()
        {
            string page = Home();
            int row0 = At(page, "<!-- Row 0: [Flame] [Type chip] Type name #N ... [X] -->");
            int row1 = At(page, "<!-- Row 1: the Preset (discussion #543)");
            int row2 = At(page, "<!-- Row 2: Device name (compact).");
            Assert.True(row0 < row1 && row1 < row2);
            string head = page.Substring(row0, row1 - row0);
            int flame = At(head, "Click=\"PowerToggle_Click\" Tag=\"{Binding PadIndex}\"");
            int chip = At(head, "Click=\"TypeChip_Click\" Tag=\"{Binding PadIndex}\"");
            int name = At(head, "<Setter Property=\"Text\" Value=\"{Binding ControllerType_Xbox, Source={x:Static strings:Strings.Instance}}\"/>");
            int number = At(head, "<TextBlock Text=\"{Binding TypeInstanceLabel, StringFormat={}#{0}}\"");
            Assert.True(flame < chip && chip < name && name < number, "the card's first line is out of order");
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
            // The delete X is still the card's, hover-revealed, one click.
            At(preset, "Click=\"DeleteSlot_Click\" Tag=\"{Binding PadIndex}\"");
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
        /// title and status, and each showing while the switch that tile
        /// shows is on. A row that read another switch would show an extra
        /// the catalog calls off.</summary>
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
                Assert.Equal(tile.GetValueOrDefault("Status"), row.GetValueOrDefault("Status"));
                // The catalog's switch writes back, Home's row only reads.
                string flag = Regex.Match(tile["IsOn"], "^\\{Binding (\\w+)\\}$").Groups[1].Value;
                Assert.False(string.IsNullOrEmpty(flag), keys[i] + "'s catalog tile has no plain IsOn binding");
                Assert.Equal("{Binding " + flag + ", Mode=OneWay}", row["IsOn"]);
                Assert.Equal("{Binding " + flag + ", Converter={StaticResource BoolToVisibilityConverter}}", row["Visibility"]);
                // Home's row has no switch, no line and no name of its own.
                Assert.False(row.ContainsKey("SwitchLabel") || row.ContainsKey("Line") || row.ContainsKey("x:Name"));
            }

            // One handler for every row, and the way to the catalog.
            string page = Home();
            At(page, "<WrapPanel x:Name=\"ExtrasInUseRows\" controls:ExtraTile.OpenRequested=\"ExtraInUse_OpenRequested\">");
            At(page, "Click=\"OpenExtras_Click\"");
            // The style gives a row its own id, so the two faces of one
            // extra never share one, and draws no switch.
            string styles = ExtrasPageTests.RepoText("PadForge.App", "Resources", "ControllerIcons.xaml");
            int style = At(styles, "<Style x:Key=\"HomeExtraTile\" TargetType=\"{x:Type controls:ExtraTile}\">");
            string home = styles.Substring(style);
            At(home, "<Setter Property=\"OpenPrefix\" Value=\"Home_Extra_\"/>");
            Assert.Equal("Home_Extra_", PadForge.Controls.ExtraTile.HomeOpenIdPrefix);
            Assert.DoesNotContain("PART_Switch", home);
            At(home, "x:Name=\"PART_Open\"");
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

        /// <summary>The section's heading and its line each have one element
        /// that changes its words with the count, so the two states never
        /// show at once.</summary>
        [Fact]
        public void TheExtrasSection_HasOneHeadingAndOneLine_ThatChangeTheirWords()
        {
            string page = Home();
            int rows = At(page, "<WrapPanel x:Name=\"ExtrasInUseRows\"");
            int heading = At(page, "<TextBlock x:Name=\"ExtrasHeading\">");
            int foot = At(page, "<WrapPanel x:Name=\"ExtrasFoot\">");
            Assert.True(heading < rows && rows < foot);
            string head = page.Substring(heading, rows - heading);
            At(head, "<Setter Property=\"Text\" Value=\"{Binding Home_ExtrasInUse, Source={x:Static strings:Strings.Instance}, Converter={StaticResource UpperConverter}}\"/>");
            Assert.Matches(new Regex("<DataTrigger Binding=\"\\{Binding ShowsExtrasIntro\\}\" Value=\"True\">\\s*"
                + "<Setter Property=\"Text\" Value=\"\\{Binding Extras_Title, "), head);

            string line = page.Substring(foot);
            At(line, "<TextBlock x:Name=\"ExtrasLine\"");
            At(line, "<Setter Property=\"Visibility\" Value=\"Collapsed\"/>");
            Assert.Matches(new Regex("<DataTrigger Binding=\"\\{Binding ShowsExtrasIntro\\}\" Value=\"True\">\\s*"
                + "<Setter Property=\"Text\" Value=\"\\{Binding Extras_Subtitle, [^\"]*\"/>\\s*"
                + "<Setter Property=\"Visibility\" Value=\"Visible\"/>"), line);
            Assert.Matches(new Regex("<DataTrigger Binding=\"\\{Binding ShowsExtrasRestLine\\}\" Value=\"True\">\\s*"
                + "<Setter Property=\"Text\" Value=\"\\{Binding Home_MoreExtras, [^\"]*\"/>\\s*"
                + "<Setter Property=\"Visibility\" Value=\"Visible\"/>"), line);
            // The button is there in every state.
            Assert.DoesNotContain("Visibility", Regex.Match(line, "<ui:Button x:Name=\"OpenExtrasButton\"[^>]*/>", RegexOptions.Singleline).Value);
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
        /// among the entries, kept by the rebuild of the slots, hidden with
        /// the closed pane and renamed with the language.</summary>
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
            At(code, "_navControllersLabel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;");
            // Both pane events tell the label and the foot.
            Assert.Equal(1, Count(code, "SetControllersLabelMode(compact: true);"));
            Assert.Equal(1, Count(code, "SetEngineFootMode(compact: true);"));
            Assert.Equal(1, Count(code, "SetControllersLabelMode(compact: false);"));
            Assert.Equal(1, Count(code, "SetEngineFootMode(compact: false);"));
            Assert.Equal(1, Count(code, "SetControllersLabelMode(compact: !NavView.IsPaneOpen);"));
            Assert.Equal(1, Count(code, "SetEngineFootMode(compact: !NavView.IsPaneOpen);"));
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
            At(engine, "<StackPanel x:Name=\"EngineFootRate\"");
            At(engine, "Text=\"{Binding PollingFrequency, StringFormat='{}{0:F0}', Mode=OneWay}\"");
            foreach (string state in new[] { "Running", "Idle", "Stopping" })
                Assert.Contains("<DataTrigger Binding=\"{Binding Dashboard.EngineStateKey}\" Value=\"" + state + "\">", engine);
            // The foot reads the window's view model, handed to it by name.
            At(code, "EngineFoot.DataContext = _viewModel;");
            At(code, "EngineFootState.Visibility = texts;");
            At(code, "EngineFootRate.Visibility = texts;");

            // The pill.
            int trailing = At(xaml, "<ui:TitleBar.TrailingContent>");
            int trailingEnd = At(xaml, "</ui:TitleBar.TrailingContent>");
            string bar = xaml.Substring(trailing, trailingEnd - trailing);
            int pill = At(bar, "<viewControls:ProfilePill x:Name=\"TitleProfilePill\" ShowLabel=\"True\"");
            At(bar, "AutomationProperties.AutomationId=\"ProfilePillStatusBar\"");
            Assert.True(pill < At(bar, "x:Name=\"FullScreenBtn\""), "the pill is not ahead of the full screen button");
            Assert.Equal(1, Count(xaml, "<viewControls:ProfilePill "));
            At(code, "ShowProfileSwitcherPopup(TitleProfilePill, PlacementMode.Bottom);");
            At(code, "TitleProfilePill.Flare();");
            string pillXaml = ExtrasPageTests.RepoText("PadForge.App", "Views", "Controls", "ProfilePill.xaml");
            At(pillXaml, "<TextBlock x:Name=\"PillLabel\" Visibility=\"Collapsed\"");
            At(pillXaml, "Text=\"{Binding Main_ProfileLabel, Source={x:Static strings:Strings.Instance}}\"");
            At(pillXaml, "<controls:GlyphText x:Name=\"PillChevron\" Visibility=\"Collapsed\"");

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
