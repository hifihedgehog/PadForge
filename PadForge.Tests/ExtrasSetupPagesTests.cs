using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using PadForge.Common.Input.KeyboardScreens;
using PadForge.Engine;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;
using Strings = PadForge.Resources.Strings.Strings;

namespace PadForge.Tests
{
    /// <summary>
    /// The Web Controller, Twitch Chat, Remote Link, Touchpad Overlay and
    /// Screen Overlay pages of Extras (#574), as their boards draw them inside
    /// the frame every extra's page shares: their cards in the boards' order,
    /// every control they had, and the parts they gained (the Phones card, a
    /// paired PC's rename in place, Manage Chat Commands, an overlay's Reset
    /// All). These read the markup and the code as text.
    /// </summary>
    public class ExtrasSetupPagesTests
    {
        private static string Page() => ExtrasPageTests.RepoText("PadForge.App", "Views", "ExtrasPage.xaml");

        /// <summary>One extra's page, from its panel to the next page's.</summary>
        private static string PageOf(string key)
        {
            string page = Page();
            var keys = PadForge.Views.ExtrasPage.Keys.ToList();
            int from = page.IndexOf("<StackPanel x:Name=\"" + key + "Page\"", StringComparison.Ordinal);
            Assert.True(from > 0, key + " has no page");
            int next = keys.IndexOf(key) + 1;
            int to = next < keys.Count
                ? page.IndexOf("<StackPanel x:Name=\"" + keys[next] + "Page\"", StringComparison.Ordinal)
                : page.Length;
            return page.Substring(from, to - from);
        }

        private static int Count(string text, string needle) =>
            text.Split(new[] { needle }, StringSplitOptions.None).Length - 1;

        public static IEnumerable<object[]> Cards() => new[]
        {
            new object[] { "WebController", new (string, string)[]
            {
                ("x:Name=\"WebControllerConnectCard\"", "Extras_WebController_ConnectTitle"),
                ("x:Name=\"WebControllerPhonesCard\"", "Extras_WebController_Phones"),
                ("x:Name=\"WebControllerMore\"", null),
            } },
            new object[] { "RemoteLink", new (string, string)[]
            {
                ("x:Name=\"RemoteLinkStatusCard\"", null),
                ("x:Name=\"RemoteLinkPairedCard\"", "RemoteLink_PairedPeers"),
                ("x:Name=\"RemoteLinkPairCard\"", "Extras_RemoteLink_PairAnother"),
                ("x:Name=\"RemoteLinkMore\"", null),
                ("{Binding Dashboard_RemoteLinkFooter,", null),
            } },
            new object[] { "TwitchChat", new (string, string)[]
            {
                ("x:Name=\"TwitchChatStatusCard\"", null),
                ("x:Name=\"TwitchChatChannelCard\"", "Dashboard_TwitchChatChannel"),
                ("x:Name=\"TwitchChatLiveCard\"", "Extras_TwitchChat_LiveTitle"),
                ("x:Name=\"TwitchChatDisplayCard\"", "Extras_TwitchChat_DisplayTitle"),
            } },
            new object[] { "TouchpadOverlay", new (string, string)[]
            {
                ("x:Name=\"TouchpadOverlayStatusCard\"", null),
                ("x:Name=\"TouchpadOverlayLookCard\"", "Extras_TouchpadOverlay_LookAndPlace"),
            } },
            new object[] { "ScreenOverlay", new (string, string)[]
            {
                ("\"ScreenOverlay_Add\"", null),
                ("{Binding ScreenOverlays}", null),
                ("{Binding Extras_ScreenOverlay_Footer,", null),
            } },
        };

        /// <summary>Each page draws its board's cards once each and in the
        /// board's order, each card under its own title.</summary>
        [Theory]
        [MemberData(nameof(Cards))]
        public void EachPage_DrawsItsBoardsCardsInOrder(string key, (string Needle, string Title)[] cards)
        {
            string body = PageOf(key);
            int before = -1;
            for (int i = 0; i < cards.Length; i++)
            {
                var (needle, title) = cards[i];
                Assert.True(Count(body, needle) == 1, key + ": " + needle + " is on the page " + Count(body, needle) + " times");
                int at = body.IndexOf(needle, StringComparison.Ordinal);
                Assert.True(at > before, key + ": " + needle + " is out of the board's order");
                before = at;
                if (title == null) continue;
                int end = i + 1 < cards.Length ? body.IndexOf(cards[i + 1].Needle, StringComparison.Ordinal) : body.Length;
                Assert.Contains("{Binding " + title + ", Source={x:Static strings:Strings.Instance}}", body.Substring(at, end - at));
            }
        }

        /// <summary>Every command and handler the five pages had is on the
        /// new pages, and the new ones are there too.</summary>
        [Fact]
        public void ThePagesKeepEveryControlTheyHad()
        {
            string page = Page();
            foreach (string name in new[]
            {
                "CopyWebControllerUrlCommand", "CopyWebControllerPlainUrlCommand", "NewWebAccessCodeCommand", "ResetWebPortCommand",
                "ResetWebPlainPortCommand", "TwitchChatPauseCommand", "TwitchChatSkipCommand", "TwitchChatClearCommand",
                "TwitchChatEndClaimCommand", "CopyTwitchChatDisplayUrlCommand", "RemoteLink.RevokeAllPeersCommand",
                "RevokeCommand", "ConnectCommand", "AssignmentsCommand", "PairCommand", "CopyRemoteLinkCodeCommand",
                "ConnectToPeerCommand", "ResetRemoteLinkPortCommand", "ResetTouchpadOverlayPositionCommand", "ChooseCommand",
                "ResetPositionCommand", "RemoveCommand", "AddScreenOverlayCommand", "ResetEnabledCommand",
                "ResetTouchpadOverlayCardCommand", "ResetAllCommand",
            })
                Assert.True(Regex.IsMatch(page, "\\{Binding " + Regex.Escape(name) + "\\}"), name + " is not on the page");
            Assert.Equal(2, Count(page, "Command=\"{Binding ResetOpacityCommand}\""));
            foreach (string handler in new[] { "ManageChatCommands_Click", "PeerRename_Click", "PeerName_KeyDown" })
            {
                Assert.Equal(1, Count(page, "=\"" + handler + "\""));
                Assert.Contains("private void " + handler + "(object sender, ", ExtrasPageTests.RepoText("PadForge.App", "Views", "ExtrasPage.xaml.cs"));
            }
        }

        /// <summary>The Twitch Chat page opens the chat's words, the dialog
        /// the Twitch Chat row on the Devices page opens.</summary>
        [Fact]
        public void TheTwitchChatPage_OpensTheChatCommands()
        {
            string body = PageOf("TwitchChat");
            var button = Regex.Match(body, "<Button AutomationProperties.AutomationId=\"TwitchChat_ManageCommands\"[^>]*>", RegexOptions.Singleline);
            Assert.True(button.Success, "no Manage Chat Commands button on the page");
            Assert.Contains("Click=\"ManageChatCommands_Click\"", button.Value);
            Assert.Contains("Content=\"{Binding TwitchChat_ManageButton, Source={x:Static strings:Strings.Instance}}\"", button.Value);
            Assert.Contains("new ChatCommandsDialog { Owner = Window.GetWindow(this) }", ExtrasPageTests.RepoText("PadForge.App", "Views", "ExtrasPage.xaml.cs"));
        }

        /// <summary>A paired PC's name reads as text and is typed over in
        /// place: Rename puts the caret in it with the name selected, the
        /// new name is kept when the focus leaves or on Enter, and the name's
        /// reset stands beside it.</summary>
        [Fact]
        public void ARemoteLinkPeer_IsRenamedInPlace()
        {
            string body = PageOf("RemoteLink");
            var rename = Regex.Match(body, "<ui:Button [^>]*Click=\"PeerRename_Click\"[^>]*>", RegexOptions.Singleline);
            Assert.True(rename.Success);
            Assert.Contains("Tag=\"{Binding ElementName=PeerNameBox}\"", rename.Value);
            Assert.Contains("Content=\"{Binding RemoteLink_Rename, Source={x:Static strings:Strings.Instance}}\"", rename.Value);

            int box = body.IndexOf("<TextBox x:Name=\"PeerNameBox\"", StringComparison.Ordinal);
            Assert.True(box > 0);
            string tag = body.Substring(box, body.IndexOf("/>", box, StringComparison.Ordinal) - box);
            Assert.Contains("Text=\"{Binding Name, Mode=TwoWay, UpdateSourceTrigger=LostFocus}\"", tag);
            Assert.Contains("MaxLength=\"40\"", tag);
            Assert.Contains("KeyDown=\"PeerName_KeyDown\"", tag);
            int dock = body.LastIndexOf("<DockPanel", box, StringComparison.Ordinal);
            int reset = body.IndexOf("AutomationProperties.AutomationId=\"Reset_RemoteLinkTrustedPeer_Name_1678\"", dock, StringComparison.Ordinal);
            Assert.True(dock > 0 && reset > dock && reset < box, "the name's reset is not beside its box");

            string code = ExtrasPageTests.RepoText("PadForge.App", "Views", "ExtrasPage.xaml.cs");
            int handler = code.IndexOf("private void PeerRename_Click(", StringComparison.Ordinal);
            string rest = code.Substring(handler, code.IndexOf("private void PeerName_KeyDown(", handler, StringComparison.Ordinal) - handler);
            Assert.Contains("box.Focus();", rest);
            Assert.Contains("box.SelectAll();", rest);
        }

        /// <summary>This PC's code and the network's verdict show in Pair
        /// Another PC only while they exist, so the card never holds an empty
        /// row or a line about nothing.</summary>
        [Fact]
        public void TheRemoteLinkPage_ShowsTheCodeAndTheVerdictOnlyWhileTheyExist()
        {
            string body = PageOf("RemoteLink");
            int card = body.IndexOf("x:Name=\"RemoteLinkPairCard\"", StringComparison.Ordinal);
            int more = body.IndexOf("x:Name=\"RemoteLinkMore\"", StringComparison.Ordinal);
            Assert.True(card > 0 && more > card);
            string pair = body.Substring(card, more - card);
            Assert.Contains("<controls:SettingRow MinHeight=\"44\" Visibility=\"{Binding HasRemoteLinkMyCode, Converter={StaticResource BoolToVisibilityConverter}}\">", pair);
            Assert.Contains("Visibility=\"{Binding RemoteLinkNatWarning, Converter={StaticResource StringToVisibility}}\"", pair);
            Assert.Contains("Text=\"{Binding RemoteLinkMyCode, Mode=OneWay}\"", pair);
            Assert.Contains("Text=\"{Binding RemoteLinkNatWarning}\"", pair);
        }

        /// <summary>The phones the Phones card lists come from the web
        /// server's own events: a session that connects is added on the UI
        /// thread while its server is the running one, a session that ends
        /// leaves, and stopping the server empties the list.</summary>
        [Fact]
        public void TheServerFeedsAndClearsThePhones()
        {
            string service = ExtrasPageTests.RepoText("PadForge.App", "Services", "InputService.cs").Replace("\r\n", "\n");
            int connected = service.IndexOf("_webServer.DeviceConnected += device =>", StringComparison.Ordinal);
            int disconnected = service.IndexOf("_webServer.DeviceDisconnected += device =>", StringComparison.Ordinal);
            Assert.True(connected > 0 && disconnected > connected);
            string onConnect = service.Substring(connected, service.IndexOf("};", connected, StringComparison.Ordinal) - connected);
            Assert.Contains("if (ReferenceEquals(_webServer, server)) _mainVm.Dashboard.AddWebControllerPhone(device);", onConnect);
            Assert.Contains("_dispatcher.BeginInvoke(", onConnect);
            Assert.Contains("var server = _webServer;", service);
            string onDisconnect = service.Substring(disconnected, service.IndexOf("};", disconnected, StringComparison.Ordinal) - disconnected);
            Assert.Contains("_mainVm.Dashboard.RemoveWebControllerPhone(device)", onDisconnect);
            int stop = service.IndexOf("private void StopWebServer()", StringComparison.Ordinal);
            Assert.True(stop > 0);
            Assert.Contains("_mainVm.Dashboard.WebControllerPhones.Clear();", service.Substring(stop, 2500));
            Assert.Contains("ItemsSource=\"{Binding WebControllerPhones}\"", PageOf("WebController"));
        }

        /// <summary>The window tells each overlay's card what it shows and
        /// resets it, and the keyboard screens' service tells the card when
        /// what it shows changes.</summary>
        [Fact]
        public void TheWindowWiresTheOverlaysChoices()
        {
            string window = ExtrasPageTests.RepoText("PadForge.App", "MainWindow.KeyboardScreens.cs");
            Assert.Contains("_viewModel.Dashboard.ScreenOverlayModeOf = number => KeyboardScreenService.OverlayMode(settings, number);", window);
            Assert.Contains("_viewModel.Dashboard.ResetScreenOverlayChoicesRequested += (_, item) => ResetScreenOverlayChoices(item);", window);
            Assert.Contains("private void ResetScreenOverlayChoices(ScreenOverlayItem item)", window);
            Assert.Contains("ChoicesChanged = row => _mainVm.Dashboard.ScreenOverlayChoicesChanged(row),",
                ExtrasPageTests.RepoText("PadForge.App", "Services", "InputService.KeyboardScreens.cs"));
        }
    }

    /// <summary>
    /// The view models behind the parts the five pages gained (#574). They read
    /// the string table, so they run with the tests that change the language.
    /// </summary>
    [Collection("CultureSwitching")]
    public class ExtrasSetupModelTests
    {
        /// <summary>A phone that reconnects under the same id is listed once,
        /// with its new session, and the end of the old session leaves the
        /// new one listed.</summary>
        [Fact]
        public void APhone_IsListedOnce_AndLeavesOnlyWithItsOwnSession()
        {
            var vm = new DashboardViewModel();
            var first = new WebControllerDevice("xbox360:phone-1", "Pad");
            var again = new WebControllerDevice("xbox360:phone-1", "Pad");
            var other = new WebControllerDevice("xbox360:phone-2", "Pad 2");

            vm.AddWebControllerPhone(first);
            Assert.Same(first, Assert.Single(vm.WebControllerPhones).Device);
            vm.AddWebControllerPhone(again);
            Assert.Same(again, Assert.Single(vm.WebControllerPhones).Device);
            vm.RemoveWebControllerPhone(first);
            Assert.Same(again, Assert.Single(vm.WebControllerPhones).Device);
            vm.RemoveWebControllerPhone(again);
            Assert.Empty(vm.WebControllerPhones);

            vm.AddWebControllerPhone(other);
            var phone = Assert.Single(vm.WebControllerPhones);
            Assert.Equal("Pad 2", phone.Name);
            Assert.Equal("Pad 2", phone.ToString());
            Assert.Equal(other.InstanceGuid, phone.Id);
            vm.RemoveWebControllerPhone(other);
            Assert.Empty(vm.WebControllerPhones);
            vm.AddWebControllerPhone(null);
            Assert.Empty(vm.WebControllerPhones);
        }

        /// <summary>The line under a paired PC's name: connected or offline,
        /// with gamepad only after it when this PC limited the PC. It follows
        /// the PC's state and the language.</summary>
        [Fact]
        public void APeersStateLine_SaysConnectedOrOffline_AndGamepadOnly()
        {
            var peer = new RemoteLinkTrustedPeer("Living Room PC", "LIVINGROOM", "0123456789ABCDEF0123", "", false, false, null, null, null);
            Assert.Equal(Strings.Instance.RemoteLink_Offline, peer.StateText);
            var raised = new List<string>();
            peer.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            peer.IsOnline = true;
            Assert.Equal(Strings.Instance.RemoteLink_Connected, peer.StateText);
            Assert.Contains(nameof(RemoteLinkTrustedPeer.StateText), raised);

            var limited = new RemoteLinkTrustedPeer("Den PC", "DEN", "FEDCBA9876543210FEDC", "", true, true, null, null, null);
            Assert.Equal(Strings.Instance.RemoteLink_Connected + "  ·  " + Strings.Instance.RemoteLink_GamepadOnlySuffix, limited.StateText);

            raised.Clear();
            peer.RefreshText();
            Assert.Contains(nameof(RemoteLinkTrustedPeer.StateText), raised);
            Assert.Contains(nameof(RemoteLinkTrustedPeer.FingerprintDisplay), raised);
        }

        /// <summary>An overlay's card is titled by its number, and its Shows
        /// row says what it shows, read from the keyboard screens' choices
        /// by its row. A change of one overlay's choices tells that card
        /// alone.</summary>
        [Fact]
        public void AnOverlaysCard_IsTitledByItsNumber_AndSaysWhatItShows()
        {
            var vm = new DashboardViewModel();
            var one = vm.ScreenOverlays[0];
            Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.Instance.Extras_ScreenOverlay_Number_Format, one.Number), one.Title);
            Assert.Equal(KeyboardScreenService.ModeName(KeyboardScreenData.DefaultMode), one.ShowsText);

            vm.AddScreenOverlay();
            var two = vm.ScreenOverlays.Last();
            Assert.NotEqual(one.Number, two.Number);
            var settings = new SettingsViewModel();
            settings.KeyboardScreens.Add(new KeyboardScreenData { Device = KeyboardScreenInfo.OverlayRow(two.Number).ToString("D"), Mode = KeyboardScreenMode.Clock });
            vm.ScreenOverlayModeOf = n => KeyboardScreenService.OverlayMode(settings, n);
            Assert.Equal(KeyboardScreenService.ModeName(KeyboardScreenMode.Clock), two.ShowsText);
            Assert.Equal(KeyboardScreenService.ModeName(KeyboardScreenData.DefaultMode), one.ShowsText);

            var ofOne = new List<string>();
            var ofTwo = new List<string>();
            one.PropertyChanged += (_, e) => ofOne.Add(e.PropertyName);
            two.PropertyChanged += (_, e) => ofTwo.Add(e.PropertyName);
            vm.ScreenOverlayChoicesChanged(KeyboardScreenInfo.OverlayRow(two.Number));
            Assert.Contains(nameof(ScreenOverlayItem.ShowsText), ofTwo);
            Assert.DoesNotContain(nameof(ScreenOverlayItem.ShowsText), ofOne);
        }

        /// <summary>The keyboard screens' service names the row of every
        /// change of choices, also one that leaves the row at every default,
        /// whose entry the change drops.</summary>
        [Fact]
        public void ChoicesChanged_FollowsEveryUpdate()
        {
            var settings = new SettingsViewModel();
            var service = new KeyboardScreenService(settings, () => { });
            var rows = new List<Guid>();
            service.ChoicesChanged = row => rows.Add(row);
            Guid two = KeyboardScreenInfo.OverlayRow(2);
            service.Update(two, d => d.Mode = KeyboardScreenMode.Clock);
            Assert.Equal(new[] { two }, rows);
            Assert.Equal(KeyboardScreenMode.Clock, KeyboardScreenService.OverlayMode(settings, 2));
            service.Update(two, d => d.Mode = KeyboardScreenData.DefaultMode);
            Assert.Equal(new[] { two, two }, rows);
            Assert.Null(service.Get(two));
            Assert.Equal(KeyboardScreenData.DefaultMode, KeyboardScreenService.OverlayMode(settings, 2));
        }

        /// <summary>An overlay's Reset All puts it back: off, at the default
        /// opacity, and it asks for its place and what it shows to go back
        /// too, each once.</summary>
        [Fact]
        public void ResetAll_PutsAnOverlayBack()
        {
            var vm = new DashboardViewModel();
            var one = vm.ScreenOverlays[0];
            one.Enabled = true;
            one.Opacity = 0.35;
            var placed = new List<ScreenOverlayItem>();
            var chosen = new List<ScreenOverlayItem>();
            vm.ResetScreenOverlayPositionRequested += (_, item) => placed.Add(item);
            vm.ResetScreenOverlayChoicesRequested += (_, item) => chosen.Add(item);
            one.ResetAllCommand.Execute(null);
            Assert.False(one.Enabled);
            Assert.Equal(ScreenOverlayItem.DefaultOpacity, one.Opacity);
            Assert.Same(one, Assert.Single(placed));
            Assert.Same(one, Assert.Single(chosen));
            Assert.Equal("ScreenOverlay_ResetAll_" + one.Number, one.ResetAllId);
        }

        /// <summary>Reset All on Look and Place puts back the touchpad
        /// overlay's opacity and asks once for its place.</summary>
        [Fact]
        public void TheTouchpadOverlaysResetAll_PutsBackOpacityAndPlace()
        {
            var vm = new DashboardViewModel();
            vm.TouchpadOverlayOpacity = 0.6;
            int placed = 0;
            vm.ResetTouchpadOverlayPositionRequested += (_, _) => placed++;
            vm.ResetTouchpadOverlayCardCommand.Execute(null);
            Assert.Equal(0.25, vm.TouchpadOverlayOpacity, 6);
            Assert.Equal(1, placed);
        }
    }
}
