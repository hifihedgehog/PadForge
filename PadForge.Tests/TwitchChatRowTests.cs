using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Common.Input.Twitch;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Models2D;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Issue #555: the Twitch Chat row, its player and chat client, its
    /// default mapping, its place in every list a device row joins, and the
    /// web controller's picture page and phone names.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public sealed class TwitchChatRowTests : IDisposable
    {
        public TwitchChatRowTests()
        {
            ChatCommandRegistry.LoadRegistry(null);
            TwitchChatRuntime.ResetForTest();
        }

        public void Dispose()
        {
            ChatCommandRegistry.LoadRegistry(null);
            TwitchChatRuntime.ResetForTest();
        }

        private static bool WaitFor(Func<bool> condition, int ms = 3000)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                if (condition()) return true;
                Thread.Sleep(5);
            }
            return condition();
        }

        private static ChatPlay Play(string user, string text)
        {
            var steps = ChatCommandParser.Parse(text, ChatCommandRegistry.ButtonFor);
            var events = ChatCommandParser.Timeline(steps, out int end);
            return new ChatPlay { User = user, Text = text, Events = events, EndMs = end };
        }

        private static bool[] Buttons(ChatCommandPlayer player)
        {
            var buttons = new bool[ChatCommandRegistry.MaxWords + 1];
            player.CopyButtons(buttons);
            return buttons;
        }

        // ── The player (ForgePlay CommandExecutor.js, DougDoug's queue) ──

        [Fact]
        public void OnlyTheNewestTwentyCommandsWait()
        {
            using var player = new ChatCommandPlayer();
            player.Enqueue(Play("first", "_a #5s"));
            Assert.True(WaitFor(() => player.IsPlaying));
            for (int i = 0; i < 25; i++) player.Enqueue(Play("v" + i, "a"));
            Assert.Equal(ChatCommandPlayer.MaxWaiting, player.WaitingCount);
            Assert.Equal("v5", player.NextWaiting(1)[0].User);
        }

        [Fact]
        public void AnyCommandIsHeldWhileACommandPlays_AndEveryButtonIsReleasedAtTheEnd()
        {
            using var player = new ChatCommandPlayer();
            player.Enqueue(Play("v", "_a #150ms"));
            Assert.True(WaitFor(() => { var b = Buttons(player); return b[0] && b[1]; }));
            Assert.True(WaitFor(() => !player.IsPlaying));
            Assert.DoesNotContain(true, Buttons(player));
        }

        [Fact]
        public void ASkipStopsTheCommand_ReleasesEverything_AndSkipAllClearsTheLine()
        {
            using var player = new ChatCommandPlayer();
            player.Enqueue(Play("v", "_a #5s"));
            player.Enqueue(Play("w", "b"));
            Assert.True(WaitFor(() => player.IsPlaying));
            Assert.True(player.Skip(all: true));
            Assert.True(WaitFor(() => !player.IsPlaying));
            Assert.Equal(0, player.WaitingCount);
            Assert.DoesNotContain(true, Buttons(player));
            Assert.False(player.Skip(all: false));
        }

        // ── Reading the chat ──

        [Fact]
        public void ALineParses_AndItsTagsAreUnescaped()
        {
            var line = IrcLine.Parse(@"@badges=broadcaster/1;display-name=Big\sBoss;mod=0 :bigboss!bigboss@bigboss.tmi.twitch.tv PRIVMSG #chan :!a500ms b");
            Assert.Equal("PRIVMSG", line.Command);
            Assert.Equal("bigboss", line.Nick);
            Assert.Equal("Big Boss", line.Tag("display-name"));
            Assert.Equal(new[] { "#chan" }, line.Params);
            Assert.Equal("!a500ms b", line.Trailing);
            Assert.Null(IrcLine.Parse("  "));
        }

        [Fact]
        public void APingIsAnsweredWithItsOwnText_AndAReconnectReconnects()
        {
            using var client = new TwitchChatClient("chan");
            Assert.Equal(TwitchChatClient.LineOutcome.None, client.Handle(IrcLine.Parse("PING :tmi.twitch.tv"), "justinfan1", out string reply));
            Assert.Equal("PONG :tmi.twitch.tv", reply);
            Assert.Equal(TwitchChatClient.LineOutcome.Reconnect, client.Handle(IrcLine.Parse(":tmi.twitch.tv RECONNECT"), "justinfan1", out _));
        }

        [Fact]
        public void JoiningReadsTheChat_AndAMessageNamesItsSenderAndWhetherAModeratorOrTheStreamerSentIt()
        {
            using var client = new TwitchChatClient("chan");
            var seen = new List<(string, bool, bool, string)>();
            client.Message += (u, m, b, t) => seen.Add((u, m, b, t));
            Assert.Equal(TwitchChatClient.LineOutcome.Joined,
                client.Handle(IrcLine.Parse(":justinfan1!justinfan1@justinfan1.tmi.twitch.tv JOIN #chan"), "justinfan1", out _));
            Assert.Equal(TwitchChatState.Reading, client.State);
            client.Handle(IrcLine.Parse("@display-name=Viewer;mod=0 :viewer!viewer@viewer.tmi.twitch.tv PRIVMSG #chan :!a"), "justinfan1", out _);
            client.Handle(IrcLine.Parse("@display-name=Helper;mod=1 :helper!helper@helper.tmi.twitch.tv PRIVMSG #chan :!skip"), "justinfan1", out _);
            // The streamer is the account whose login is the channel's name
            // (ComfyJS app.js 1039), with or without a badge.
            client.Handle(IrcLine.Parse("@badges=broadcaster/1;display-name=Chan;mod=0 :chan!chan@chan.tmi.twitch.tv PRIVMSG #chan :!skipall"), "justinfan1", out _);
            client.Handle(IrcLine.Parse("@display-name=CHAN;mod=0 :chan!chan@chan.tmi.twitch.tv PRIVMSG #chan :!b"), "justinfan1", out _);
            // A badge on another login is not the streamer.
            client.Handle(IrcLine.Parse("@badges=broadcaster/1;display-name=Other;mod=0 :other!other@other.tmi.twitch.tv PRIVMSG #chan :!x"), "justinfan1", out _);
            Assert.Equal(new[]
            {
                ("Viewer", false, false, "!a"), ("Helper", true, false, "!skip"), ("Chan", true, true, "!skipall"),
                ("CHAN", true, true, "!b"), ("Other", false, false, "!x"),
            }, seen);
        }

        [Theory]
        [InlineData("SomeStreamer", "somestreamer")]
        [InlineData("https://www.twitch.tv/SomeStreamer/", "somestreamer")]
        [InlineData("twitch.tv/some_streamer", "some_streamer")]
        [InlineData("#chan", "chan")]
        [InlineData("m.twitch.tv/chan", "chan")]
        [InlineData("not a channel", "")]
        [InlineData("", "")]
        [InlineData("abcdefghijklmnopqrstuvwxyz", "")]
        public void AChannelIsReadFromItsNameOrItsAddress(string typed, string channel)
            => Assert.Equal(channel, TwitchChatClient.NormalizeChannel(typed));

        [Fact]
        public void InTheTestHostTheClientNeverReachesTwitch()
        {
            int sockets = 0;
            using var client = new TwitchChatClient("chan", () => { sockets++; throw new InvalidOperationException(); });
            client.Start();
            Assert.True(WaitFor(() => client.State == TwitchChatState.Retrying));
            Assert.Equal(0, sockets);
        }

        // ── The row ──

        private static TwitchChatDevice Row()
        {
            var row = new TwitchChatDevice(string.Empty, 0);
            row.AttachForTest();
            return row;
        }

        private static bool Pressed(TwitchChatDevice row, int button) => row.GetCurrentState().Buttons[button];

        [Fact]
        public void TheRowKeepsOneIdentity_AndItsButtonsAreTheWords()
        {
            using var row = Row();
            using var other = new TwitchChatDevice("somechannel", 3);
            Assert.Equal(InputDeviceType.TwitchChat, row.GetInputDeviceType());
            Assert.Equal("twitch://chat", row.DevicePath);
            Assert.Equal(TwitchChatDevice.Identity, row.InstanceGuid);
            Assert.Equal(row.InstanceGuid, other.InstanceGuid);
            Assert.Equal(17, row.NumButtons);
            var objects = row.GetDeviceObjects();
            Assert.Equal(TwitchChatDevice.AnyCommandName, objects[0].Name);
            Assert.Equal(0, objects[0].InputIndex);
            Assert.Equal("!a", objects[1].Name);
            Assert.Equal(1, objects[1].InputIndex);

            ChatCommandRegistry.Remove("b");
            ChatCommandRegistry.Add("jump");
            ChatCommandRegistry.Add("dash");
            Assert.Contains(row.GetDeviceObjects(), o => o.Name == "!jump" && o.InputIndex == 2);
            Assert.Equal(18, row.NumButtons);
            Assert.Equal(new[] { 0 }.Concat(ChatCommandRegistry.Words.Select(w => w.Button)), row.SupportedButtonIndices);
        }

        [Fact]
        public void ACommandPlaysOnTheRow_AndChatThatIsNotOneIsIgnored()
        {
            using var row = Row();
            row.HandleMessage("viewer", false, "hello there");
            row.HandleMessage("viewer", false, "!nope");
            row.HandleMessage("viewer", false, "!");
            var idle = row.Snapshot();
            Assert.Null(idle.PlayingUser);
            Assert.Equal(0, idle.Waiting);

            // ComfyJS lower-cases the first word before ForgePlay reads it,
            // so a time in capitals plays there. ForgePlay's own time pattern
            // is case-sensitive, so without that the unit would not parse.
            row.HandleMessage("viewer", false, "!A5S");
            Assert.True(WaitFor(() => Pressed(row, 0) && Pressed(row, 1)));
            Assert.Equal("viewer", row.Snapshot().PlayingUser);
            Assert.Equal("A5S", row.Snapshot().PlayingText);
        }

        [Fact]
        public void AReplysCommandPlays_AsComfyJsReadsIt()
        {
            using var row = Row();
            row.HandleMessage("viewer", false, "@someone !_b #5s");
            Assert.True(WaitFor(() => Pressed(row, 2)));
        }

        [Fact]
        public void APauseStopsTheCommandPlaying_AndIgnoresChatUntilResumed()
        {
            using var row = Row();
            TwitchChatRuntime.SetLive(row);
            row.HandleMessage("v", false, "!_a #5s");
            Assert.True(WaitFor(() => Pressed(row, 1)));
            TwitchChatRuntime.Paused = true;
            Assert.True(WaitFor(() => !Pressed(row, 0) && !Pressed(row, 1)));
            row.HandleMessage("v", false, "!a");
            Assert.Equal(0, row.Snapshot().Waiting);
            Assert.True(row.Snapshot().Paused);
            TwitchChatRuntime.Paused = false;
            row.HandleMessage("v", false, "!_a #5s");
            Assert.True(WaitFor(() => Pressed(row, 1)));
        }

        [Fact]
        public void WhileAViewerHoldsControl_OnlyTheirCommandsAndModeratorsPlay()
        {
            using var row = Row();
            row.HandleMessage("Mod", true, "!claim @Owner");
            Assert.Null(row.Snapshot().Owner);
            row.HandleMessage("Host", true, "!claim @Owner", broadcaster: true);
            Assert.Equal("Owner", row.Snapshot().Owner);
            row.HandleMessage("Other", false, "!a");
            Assert.Null(row.Snapshot().PlayingUser);
            Assert.Equal(0, row.Snapshot().Waiting);
            row.HandleMessage("owner", false, "!_a #5s");
            Assert.True(WaitFor(() => row.Snapshot().PlayingUser == "owner"));
        }

        [Fact]
        public void TheRuntimeReadsTheChannelAsTwitchNamesIt_AndReopensOnlyWhileOn()
        {
            int v = TwitchChatRuntime.Version;
            TwitchChatRuntime.Channel = "twitch.tv/Somebody";
            Assert.Equal("somebody", TwitchChatRuntime.Channel);
            Assert.Equal(v, TwitchChatRuntime.Version);
            TwitchChatRuntime.Enabled = true;
            Assert.Equal(v + 1, TwitchChatRuntime.Version);
            TwitchChatRuntime.Channel = "other";
            Assert.Equal(v + 2, TwitchChatRuntime.Version);
            TwitchChatRuntime.Channel = "OTHER";
            Assert.Equal(v + 2, TwitchChatRuntime.Version);
        }

        // ── The default mapping ──

        [Fact]
        public void AssigningTheRow_MapsTheUsualWordsToTheButtonsTheyName()
        {
            using var row = Row();
            var ud = new UserDevice();
            ud.LoadFromExternalDevice(row);
            var ps = SettingsManager.CreateDefaultPadSetting(ud, VirtualControllerType.Xbox);
            Assert.Equal("Button 1", ps.ButtonA);
            Assert.Equal("Button 2", ps.ButtonB);
            Assert.Equal("Button 3", ps.ButtonX);
            Assert.Equal("Button 4", ps.ButtonY);
            Assert.Equal("Button 5", ps.LeftShoulder);
            Assert.Equal("Button 6", ps.RightShoulder);
            Assert.Equal("Button 7", ps.LeftTrigger);
            Assert.Equal("Button 8", ps.RightTrigger);
            Assert.Equal("Button 9", ps.ButtonBack);
            Assert.Equal("Button 10", ps.ButtonStart);
            Assert.Equal("Button 11", ps.LeftThumbButton);
            Assert.Equal("Button 12", ps.RightThumbButton);
            Assert.Equal("Button 13", ps.DPadUp);
            Assert.Equal("Button 14", ps.DPadDown);
            Assert.Equal("Button 15", ps.DPadLeft);
            Assert.Equal("Button 16", ps.DPadRight);
            Assert.True(string.IsNullOrEmpty(ps.ButtonGuide));
            Assert.True(string.IsNullOrEmpty(ps.LeftThumbAxisX));
        }

        [Fact]
        public void ARemovedUsualWordBindsNothing_AndANintendoSlotTakesTheDPadFromTheWords()
        {
            using var row = Row();
            ChatCommandRegistry.Remove("x");
            var ud = new UserDevice();
            ud.LoadFromExternalDevice(row);
            Assert.True(string.IsNullOrEmpty(SettingsManager.CreateDefaultPadSetting(ud, VirtualControllerType.Xbox).ButtonX));
            var ps = SettingsManager.CreateDefaultPadSetting(ud, VirtualControllerType.Nintendo);
            Assert.Equal("Button 13", ps.GetRawMapping("RawPov0Up"));
            Assert.Equal("Button 16", ps.GetRawMapping("RawPov0Right"));
            // Positional, as for any pad: the south word lands on Switch B.
            Assert.Equal("Button 1", ps.GetRawMapping("RawBtn0"));
        }

        // ── Every list a device row joins ──

        [Fact]
        public void TheTypeNumberIsPinned_AndTheRowJoinsEverySiblingList()
        {
            Assert.Equal(45, InputDeviceType.TwitchChat);
            Assert.False(InputDeviceType.AnswersAnyDeviceSources(InputDeviceType.TwitchChat));
            Assert.Equal("", DeviceTypeGlyph.For(InputDeviceType.TwitchChat));
            var row = new DeviceRowViewModel { DeviceTypeKey = "TwitchChat", DevicePath = "twitch://chat" };
            Assert.True(row.IsInternalVirtual);
            Assert.False(row.ShowSubmitMapping);
            Assert.True(row.ShowManageChatCommands);
            Assert.Equal(Strings.Instance.DeviceType_TwitchChat, row.DeviceType);
            Assert.False(new DeviceRowViewModel { DeviceTypeKey = "TwitchChat", DevicePath = "peer://host/twitch" }.ShowManageChatCommands);
            Assert.False(new DeviceRowViewModel { DeviceTypeKey = "Microphone", DevicePath = "mic://x" }.ShowManageChatCommands);
            Assert.False(MappingDisplayResolver.UseRawNumberedNaming(new UserDevice { CapType = InputDeviceType.TwitchChat }));
            Assert.Equal(Strings.Instance.TwitchChat_AnyCommand, MappingDisplayResolver.LocalizeObjectName(TwitchChatDevice.AnyCommandName));
        }

        [Fact]
        public void TheDashboardSwitchAndChannelReachTheRuntime_AndOffClearsAPause()
        {
            var dash = new DashboardViewModel();
            dash.TwitchChatChannel = "https://twitch.tv/Streamer";
            dash.EnableTwitchChat = true;
            Assert.True(TwitchChatRuntime.Enabled);
            Assert.Equal("streamer", TwitchChatRuntime.Channel);
            dash.TwitchChatPaused = true;
            Assert.True(TwitchChatRuntime.Paused);
            dash.EnableTwitchChat = false;
            Assert.False(TwitchChatRuntime.Enabled);
            Assert.False(TwitchChatRuntime.Paused);
            Assert.True(DashboardViewModel.CanResetSetting(nameof(DashboardViewModel.EnableTwitchChat)));
        }

        [Fact]
        public void TheStatusLineSaysWhatTheRowIsDoing()
        {
            var s = Strings.Instance;
            Assert.Equal(s.TwitchChat_Status_NoChannel, InputService.BuildTwitchChatStatus("", null, false));
            Assert.Equal(string.Format(s.TwitchChat_Status_Reading_Format, "chan"), InputService.BuildTwitchChatStatus("chan", TwitchChatState.Reading, false));
            Assert.Equal(string.Format(s.TwitchChat_Status_Retrying_Format, "chan") + " " + s.TwitchChat_Status_Paused,
                InputService.BuildTwitchChatStatus("chan", TwitchChatState.Retrying, true));
        }

        // ── The web controller ──

        [Theory]
        [InlineData(null, null)]
        [InlineData("   ", null)]
        [InlineData(" Alex ", "Alex")]
        [InlineData("A‮lex\u0007", "Alex")]
        [InlineData("abcdefghijklmnopqrstuvwxyz", "abcdefghijklmnopqrstuvwx")]
        public void AViewersNameIsCleanedForTheDeviceRow(string raw, string name)
            => Assert.Equal(name, WebControllerServer.CleanViewerName(raw));

        [Fact]
        public void ThePicturePageWatchesTheNamedSlot_ElseTheChatRowsFirst_ElseSlotOne()
        {
            using var server = new WebControllerServer(_ => null, () => null);
            Assert.Equal(2, server.DisplaySlotFor("3"));
            Assert.Equal(0, server.DisplaySlotFor("auto"));
            server.DisplayChatSlot = () => 5;
            Assert.Equal(5, server.DisplaySlotFor("auto"));
            Assert.Equal(5, server.DisplaySlotFor("17"));
            Assert.Equal(5, server.DisplaySlotFor("0"));
        }

        [Fact]
        public void TheChatRowsFirstSlotIsTheLowestItIsAssignedTo()
        {
            var settings = new[]
            {
                new UserSetting { InstanceGuid = TwitchChatDevice.Identity, MapTo = 6 },
                new UserSetting { InstanceGuid = TwitchChatDevice.Identity, MapTo = 2 },
                new UserSetting { InstanceGuid = Guid.NewGuid(), MapTo = 0 },
            };
            var saved = SettingsManager.UserSettings;
            SettingsManager.UserSettings = new SettingsCollection();
            try
            {
                Assert.Equal(-1, InputService.FirstTwitchChatSlot());
                lock (SettingsManager.UserSettings.SyncRoot)
                    foreach (var us in settings) SettingsManager.UserSettings.Items.Add(us);
                Assert.Equal(2, InputService.FirstTwitchChatSlot());
            }
            finally
            {
                SettingsManager.UserSettings = saved;
            }
        }

        [Fact]
        public void AGamepadSlotLightsWhatThePadPageLights()
        {
            var gp = new Gamepad
            {
                Buttons = (ushort)(Gamepad.A | Gamepad.DPAD_LEFT | Gamepad.TOUCHPAD),
                LeftTrigger = 65535,
                ThumbLX = short.MinValue,
                ThumbRY = short.MaxValue,
                Share = true,
            };
            var pose = DisplayPose.FromGamepad(gp);
            // The touchpad bit is the macros' and the pad page lights no art
            // for it, so the picture does not either.
            Assert.Equal(new[] { "ButtonA", "DPadLeft", "ButtonShare" }, pose.Lit);
            Assert.Equal(1.0, pose.LeftTrigger);
            Assert.Equal(-1.0, pose.LeftX);
            Assert.Equal(1.0, pose.RightY);
        }

        [Fact]
        public void ASwitchSlotLightsItsWireThroughThePadPagesTable()
        {
            var raw = RawHidState.Create(4, 16, 1);
            raw.SetButton(NintendoPreviewMap.IndexOf("switch-pro", "ButtonA"), true);
            raw.SetButton(NintendoPreviewMap.IndexOf("switch-pro", "LeftTrigger"), true);
            raw.Povs[0] = 27000;
            raw.Axes[1] = short.MaxValue; // HID down
            var pose = DisplayPose.FromRaw(raw, "switch-pro");
            Assert.Equal(new[] { "ButtonA", "DPadLeft" }, pose.Lit);
            // ZL is digital: a full pull.
            Assert.Equal(1.0, pose.LeftTrigger);
            Assert.Equal(-1.0, pose.LeftY, 3);
        }

        [Fact]
        public void TheDashboardsApprovalChoiceReachesTheRow_ResetsToTheStreamer_AndIgnoresAnythingOutOfRange()
        {
            Assert.Equal(0, new PadForge.Services.AppSettingsData().TwitchChatClaimApproval);
            var vm = new PadForge.ViewModels.DashboardViewModel();
            Assert.Equal((int)TwitchClaimApproval.Streamer, vm.TwitchChatClaimApprovalIndex);
            vm.TwitchChatClaimApprovalIndex = (int)TwitchClaimApproval.Viewers;
            Assert.Equal(TwitchClaimApproval.Viewers, TwitchChatRuntime.ClaimApproval);
            vm.TwitchChatClaimApprovalIndex = -1;
            vm.TwitchChatClaimApprovalIndex = 3;
            Assert.Equal((int)TwitchClaimApproval.Viewers, vm.TwitchChatClaimApprovalIndex);
            Assert.Equal(TwitchClaimApproval.Viewers, TwitchChatRuntime.ClaimApproval);
            vm.ResetSettingCommand.Execute(nameof(vm.TwitchChatClaimApprovalIndex));
            Assert.Equal(TwitchClaimApproval.Streamer, TwitchChatRuntime.ClaimApproval);
        }

        [Fact]
        public void TheChatMessageCarriesWhoIsPlaying_AndSaysWhenChatIsOff()
        {
            Assert.Contains("\"on\":false", System.Text.Json.JsonSerializer.Serialize(WebControllerServer.ChatMessage(null)));
            using var row = Row();
            row.HandleMessage("Viewer", false, "!_a #5s");
            Assert.True(WaitFor(() => row.Snapshot().PlayingUser != null));
            string json = System.Text.Json.JsonSerializer.Serialize(WebControllerServer.ChatMessage(row.Snapshot()));
            Assert.Contains("\"user\":\"Viewer\"", json);
            Assert.Contains("\"state\":\"nochannel\"", json);
            // No count while only the streamer approves, the default.
            Assert.Contains("\"votesNeeded\":0", json);
            int shown = row.ShowVersion;
            TwitchChatRuntime.ClaimApproval = TwitchClaimApproval.Viewers;
            Assert.NotEqual(shown, row.ShowVersion);
            json = System.Text.Json.JsonSerializer.Serialize(WebControllerServer.ChatMessage(row.Snapshot()));
            Assert.Contains("\"votesNeeded\":2", json);
        }

        // ── The web assets ──

        private static string RepoRoot()
        {
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 10 && dir != null; i++)
            {
                if (File.Exists(Path.Combine(dir, "PadForge.sln"))) return dir;
                dir = Path.GetDirectoryName(dir);
            }
            throw new InvalidOperationException("PadForge.sln not found");
        }

        private static string Web(string path) => File.ReadAllText(Path.Combine(RepoRoot(), "PadForge.App", "WebAssets", path));

        [Fact]
        public void TheHomePagesCardsDrawEachControllerAtRest_TriggersIncluded()
        {
            string index = Web("index.html");
            Assert.DoesNotContain("img class=\"preview\"", index);
            Assert.Equal(10, System.Text.RegularExpressions.Regex.Matches(index, "class=\"preview\" data-layout=").Count);
            Assert.Contains("/js/layout_view.js", index);
            string view = Web(Path.Combine("js", "layout_view.js"));
            // The rest silhouette for a trigger with no base of its own, the
            // controller page's rule (controller_client.js buildController).
            Assert.Contains("ov.type === \"trigger\" && !baseFor[ov.target] && !baseDrawsTriggers", view);
            Assert.Contains("rest.style.opacity = \"0.45\"", view);
            Assert.Contains("ov.type === \"trigger\" && !baseFor[ov.target] && !baseDrawsTriggers", Web(Path.Combine("js", "controller_client.js")));
        }

        [Fact]
        public void ThePhonePagesSendTheViewersName()
        {
            Assert.Contains("viewerName", Web("index.html"));
            foreach (var page in new[] { "controller_client.js", "gamepad_client.js", "custom_client.js" })
                Assert.Contains("\"&name=\" + encodeURIComponent(viewerName)", Web(Path.Combine("js", page)));
        }

        [Fact]
        public void EveryWlgDrawingShipsBesideTheStockArt()
        {
            string models = Path.Combine(RepoRoot(), "PadForge.App", "2DModels");
            foreach (var (folder, basePath, layout) in new (string, string, OverlayElement[])[]
                     {
                         ("DualSense", DualSenseLayout.BasePath, DualSenseLayout.Overlays),
                         ("XBOX360", Xbox360Layout.BasePath, Xbox360Layout.Overlays),
                         ("XBOXSERIES", XboxSeriesXLayout.BasePath, XboxSeriesXLayout.Overlays),
                     })
            {
                var files = layout.Where(o => !string.IsNullOrEmpty(o.ImageFile)).Select(o => o.ImageFile)
                    .Append(Path.GetFileName(basePath));
                foreach (string file in files)
                {
                    if (!File.Exists(Path.Combine(models, folder, file))) continue;
                    string wlg = Path.Combine(models, folder, Path.GetFileNameWithoutExtension(file) + "_WLG.png");
                    Assert.True(File.Exists(wlg), wlg);
                }
            }
        }
    }
}
