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

        [Fact]
        public void TheButtonsStayUpForTheGapBetweenTwoCommands()
        {
            using var player = new ChatCommandPlayer();
            player.Enqueue(Play("v", "a1ms"));
            player.Enqueue(Play("w", "a1ms"));
            Assert.True(WaitFor(() => player.LastGapMs > 0 && player.WaitingCount == 0));
            Assert.True(player.LastGapMs >= ChatCommandParser.ReleaseGapMs, $"{player.LastGapMs} ms");
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
            client.Message += (_, u, m, b, t) => seen.Add((u, m, b, t));
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

        [Fact]
        public void AMessageCarriesTheSendersLoginBesideTheNameShown()
        {
            using var client = new TwitchChatClient("chan");
            string login = null, shown = null;
            client.Message += (l, u, _, _, _) => { login = l; shown = u; };
            client.Handle(IrcLine.Parse("@display-name=\u5C0F\u660E;mod=0 :xiaoming!xiaoming@xiaoming.tmi.twitch.tv PRIVMSG #chan :!a"), "justinfan1", out _);
            Assert.Equal("xiaoming", login);
            Assert.Equal("\u5C0F\u660E", shown);
        }

        [Fact]
        public void AMessageTwitchSendsTwiceCountsOnce_AndOneWithNoIdAlwaysCounts()
        {
            using var client = new TwitchChatClient("chan");
            int seen = 0;
            client.Message += (_, _, _, _, _) => seen++;
            string line = "@id=b34ccfc7-4977-403a-8a94-33c6bac34fb8;mod=0 :v!v@v.tmi.twitch.tv PRIVMSG #chan :!a";
            Assert.Equal(TwitchChatClient.LineOutcome.Message, client.Handle(IrcLine.Parse(line), "justinfan1", out _));
            Assert.Equal(TwitchChatClient.LineOutcome.None, client.Handle(IrcLine.Parse(line), "justinfan1", out _));
            Assert.Equal(1, seen);
            client.Handle(IrcLine.Parse("@mod=0 :v!v@v.tmi.twitch.tv PRIVMSG #chan :!a"), "justinfan1", out _);
            client.Handle(IrcLine.Parse("@mod=0 :v!v@v.tmi.twitch.tv PRIVMSG #chan :!a"), "justinfan1", out _);
            Assert.Equal(3, seen);
        }

        [Fact]
        public void OnlyTheLatestMessageIdsAreRemembered()
        {
            using var client = new TwitchChatClient("chan");
            int seen = 0;
            client.Message += (_, _, _, _, _) => seen++;
            void Send(int id) => client.Handle(IrcLine.Parse($"@id={id} :v!v@v.tmi.twitch.tv PRIVMSG #chan :!a"), "justinfan1", out _);
            for (int i = 0; i <= TwitchChatClient.RememberedIds; i++) Send(i);
            Assert.Equal(TwitchChatClient.RememberedIds + 1, seen);
            Send(TwitchChatClient.RememberedIds);
            Assert.Equal(TwitchChatClient.RememberedIds + 1, seen);
            Send(0);
            Assert.Equal(TwitchChatClient.RememberedIds + 2, seen);
        }

        [Theory]
        [InlineData("msg_channel_suspended")]
        [InlineData("msg_room_not_found")]
        [InlineData("tos_ban")]
        public void ANoticeThatTheChannelCannotBeReadReadsAsNoChat(string id)
        {
            using var client = new TwitchChatClient("chan");
            client.Handle(IrcLine.Parse($"@msg-id={id} :tmi.twitch.tv NOTICE #chan :No."), "justinfan1", out _);
            Assert.Equal(TwitchChatState.NoChat, client.State);
        }

        /// <summary>A channel suspended after a good join reads as no chat and is
        /// asked for again, by the NOTICE or by a PART of this session's, until it
        /// is back. A PART of anyone else's changes nothing.</summary>
        [Theory]
        [InlineData("@msg-id=msg_channel_suspended :tmi.twitch.tv NOTICE #chan :No.")]
        [InlineData(":{nick}!{nick}@{nick}.tmi.twitch.tv PART #chan")]
        public void AChannelLostAfterAGoodJoin_IsAskedForAgain(string line)
        {
            using var client = new TwitchChatClient("chan") { JoinAnswerPeriodMs = 20, KeepAlivePeriodMs = 20 };
            var socket = new ScriptedSocket { Answer = JoinAnswer };
            using var stop = new CancellationTokenSource();
            var session = client.ReadAsync(socket, stop.Token);
            Assert.True(WaitFor(() => client.State == TwitchChatState.Reading), "the state stayed " + client.State);
            int joins = socket.Count("JOIN #chan");

            socket.Answer = null;
            socket.Say(":someone!someone@x PART #chan");
            Assert.False(WaitFor(() => client.State != TwitchChatState.Reading, 100));
            socket.Say(line.Replace("{nick}", socket.Nick));
            Assert.True(WaitFor(() => client.State == TwitchChatState.NoChat), "the state stayed " + client.State);
            Assert.True(WaitFor(() => socket.Count("JOIN #chan") > joins), "no JOIN after the channel was lost");

            socket.Answer = JoinAnswer;
            Assert.True(WaitFor(() => client.State == TwitchChatState.Reading), "the state stayed " + client.State);
            stop.Cancel();
            Assert.True(WaitFor(() => session.IsCompleted));
        }

        [Fact]
        public void ANoticeAboutAnythingElseLeavesTheState()
        {
            using var client = new TwitchChatClient("chan");
            int before = client.StatusVersion;
            client.Handle(IrcLine.Parse("@msg-id=slow_on :tmi.twitch.tv NOTICE #chan :Slow."), "justinfan1", out _);
            Assert.Equal(TwitchChatState.Connecting, client.State);
            Assert.Equal(before, client.StatusVersion);
        }

        /// <summary>A socket that plays a script: what it was sent, and an
        /// answer to each line it is given. A receive waits for an answer or
        /// the close, as a quiet channel does.</summary>
        private sealed class ScriptedSocket : IChatSocket
        {
            private readonly System.Threading.Channels.Channel<string> _frames =
                System.Threading.Channels.Channel.CreateUnbounded<string>();
            private readonly List<string> _sent = new();
            private int _receives;
            private string _nick;

            public Func<string, string, string> Answer;
            public bool ConnectNeverEnds;

            public int Receives => Volatile.Read(ref _receives);
            public string Nick => _nick;
            public int Count(string line) { lock (_sent) return _sent.Count(s => s == line); }
            public void Say(string frame) => _frames.Writer.TryWrite(frame);
            public void Close() => _frames.Writer.TryComplete();

            public Task ConnectAsync(Uri uri, CancellationToken ct)
                => ConnectNeverEnds ? Task.Delay(Timeout.Infinite, ct) : Task.CompletedTask;

            public Task SendAsync(string line, CancellationToken ct)
            {
                lock (_sent) _sent.Add(line);
                if (line.StartsWith("NICK ", StringComparison.Ordinal)) _nick = line.Substring(5);
                string answer = Answer?.Invoke(line, _nick);
                if (answer != null) Say(answer);
                return Task.CompletedTask;
            }

            public async Task<string> ReceiveAsync(CancellationToken ct)
            {
                Interlocked.Increment(ref _receives);
                try { return await _frames.Reader.ReadAsync(ct); }
                catch (System.Threading.Channels.ChannelClosedException) { return null; }
            }

            public void Dispose() { }
        }

        private static string JoinAnswer(string line, string nick)
            => line == "JOIN #chan" ? $":{nick}!{nick}@{nick}.tmi.twitch.tv JOIN #chan" : null;

        private static string PongAndJoinAnswer(string line, string nick)
            => line.StartsWith("PING", StringComparison.Ordinal) ? ":tmi.twitch.tv PONG tmi.twitch.tv :tmi.twitch.tv" : JoinAnswer(line, nick);

        [Fact]
        public void AQuietChannelGetsItsPingOnTime_WithTheOneReceiveLeftOpen()
        {
            using var client = new TwitchChatClient("chan") { KeepAlivePeriodMs = 20 };
            var socket = new ScriptedSocket();
            using var stop = new CancellationTokenSource();
            var session = client.ReadAsync(socket, stop.Token);
            Assert.True(WaitFor(() => socket.Count("PING :tmi.twitch.tv") >= 3), "no PING came");
            // Canceling a receive ends a real socket, so each wake for a PING
            // leaves the first one running.
            Assert.Equal(1, socket.Receives);
            Assert.False(session.IsCompleted);
            stop.Cancel();
            Assert.True(WaitFor(() => session.IsCompleted));
        }

        [Fact]
        public void ALinkThatAnswersItsPingsOutlastsTheSilenceLimit()
        {
            using var client = new TwitchChatClient("chan") { KeepAlivePeriodMs = 50, SilencePeriodMs = 1500 };
            var socket = new ScriptedSocket { Answer = PongAndJoinAnswer };
            using var stop = new CancellationTokenSource();
            var session = client.ReadAsync(socket, stop.Token);
            var clock = Stopwatch.StartNew();
            Assert.True(WaitFor(() => clock.ElapsedMilliseconds > 2000, 5000));
            Assert.False(session.IsCompleted, session.Exception?.ToString());
            Assert.Equal(TwitchChatState.Reading, client.State);
            stop.Cancel();
            Assert.True(WaitFor(() => session.IsCompleted));
        }

        [Fact]
        public async System.Threading.Tasks.Task ALinkThatStopsAnsweringEndsAfterTheSilenceLimit()
        {
            using var client = new TwitchChatClient("chan") { KeepAlivePeriodMs = 20, SilencePeriodMs = 150 };
            var socket = new ScriptedSocket { Answer = JoinAnswer };
            var ex = await Assert.ThrowsAsync<TimeoutException>(() => client.ReadAsync(socket, CancellationToken.None));
            Assert.Equal("Twitch stopped answering.", ex.Message);
            Assert.True(socket.Count("PING :tmi.twitch.tv") >= 1);
        }

        [Fact]
        public async System.Threading.Tasks.Task AConnectionThatNeverFinishesFailsAfterItsLimit()
        {
            using var client = new TwitchChatClient("chan") { ConnectPeriodMs = 30 };
            var socket = new ScriptedSocket { ConnectNeverEnds = true };
            using var stop = new CancellationTokenSource();
            var session = client.ReadAsync(socket, stop.Token);
            bool ended = await System.Threading.Tasks.Task.WhenAny(session, System.Threading.Tasks.Task.Delay(5000)) == session;
            stop.Cancel();
            Assert.True(ended, "the connect was still waiting");
            await Assert.ThrowsAsync<TimeoutException>(() => session);
        }

        [Fact]
        public void AJoinNobodyAnswersReadsAsNoChat_IsAskedAgain_AndALateAnswerReads()
        {
            using var client = new TwitchChatClient("chan") { JoinAnswerPeriodMs = 20, KeepAlivePeriodMs = 20 };
            var socket = new ScriptedSocket();
            using var stop = new CancellationTokenSource();
            var session = client.ReadAsync(socket, stop.Token);
            Assert.True(WaitFor(() => client.State == TwitchChatState.NoChat), "the state stayed " + client.State);
            Assert.True(WaitFor(() => socket.Count("JOIN #chan") >= 3), "no second JOIN");
            Assert.False(session.IsCompleted);
            socket.Say(":justinfan1!justinfan1@x JOIN #chan");
            // The nick in that line is not this session's, so it changes nothing.
            Assert.False(WaitFor(() => client.State == TwitchChatState.Reading, 100));
            socket.Answer = JoinAnswer;
            Assert.True(WaitFor(() => client.State == TwitchChatState.Reading), "the state stayed " + client.State);
            stop.Cancel();
            Assert.True(WaitFor(() => session.IsCompleted));
        }

        [Fact]
        public async System.Threading.Tasks.Task TheRetryDelayDoublesUntilASessionJoins_ThenStartsOver()
        {
            using var client = new TwitchChatClient("chan");
            using var stop = new CancellationTokenSource();
            var delays = new List<int>();
            int session = 0;
            async System.Threading.Tasks.Task<bool> Session(CancellationToken ct)
            {
                switch (++session)
                {
                    case 4:
                        // A healthy session: it joins, reads, and then the socket closes.
                        var socket = new ScriptedSocket();
                        socket.Answer = (line, nick) =>
                        {
                            string answer = JoinAnswer(line, nick);
                            if (answer != null) { socket.Say(answer); socket.Close(); }
                            return null;
                        };
                        return await client.ReadAsync(socket, ct);
                    case 7:
                        stop.Cancel();
                        return false;
                    default:
                        throw new InvalidOperationException("No.");
                }
            }
            await client.RunAsync(Session, (ms, _) => { delays.Add(ms); return System.Threading.Tasks.Task.CompletedTask; }, stop.Token);
            Assert.Equal(new[] { 1000, 2000, 4000, 1000, 2000, 4000 }, delays);
        }

        [Fact]
        public async System.Threading.Tasks.Task TheRetryDelayStopsAtThirtySeconds()
        {
            using var client = new TwitchChatClient("chan");
            using var stop = new CancellationTokenSource();
            var delays = new List<int>();
            await client.RunAsync(_ => throw new InvalidOperationException("No."),
                (ms, _) =>
                {
                    delays.Add(ms);
                    if (delays.Count == 7) stop.Cancel();
                    return System.Threading.Tasks.Task.CompletedTask;
                }, stop.Token);
            Assert.Equal(new[] { 1000, 2000, 4000, 8000, 16000, 30000, 30000 }, delays);
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
        public void TakingControlTakesTheLine_OtherViewersCommandsStopAndLeave_ModeratorsStay()
        {
            using var row = Row();
            row.HandleMessage("Playing", false, "!_a #5s");
            Assert.True(WaitFor(() => row.Snapshot().PlayingUser == "Playing"));
            row.HandleMessage("Waiting", false, "!_b #5s");
            row.HandleMessage("Mod", true, "!_x #5s");
            row.HandleMessage("Owner", false, "!_y #5s");
            row.HandleMessage("Host", true, "!claim @Owner", broadcaster: true);

            // The moderator's command plays next, and the owner's waits behind it.
            Assert.True(WaitFor(() => row.Snapshot().PlayingUser == "Mod"), "still " + row.Snapshot().PlayingUser);
            var snapshot = row.Snapshot();
            Assert.Equal(new[] { "Owner" }, snapshot.Next.Select(n => n.User));
            Assert.Equal(1, snapshot.Waiting);
            Assert.False(Pressed(row, ChatCommandRegistry.ButtonFor("a")));
        }

        [Fact]
        public void TheOwnersOwnPlayingCommandSurvivesTheGrant()
        {
            using var row = Row();
            row.HandleMessage("Owner", false, "!_a #5s");
            Assert.True(WaitFor(() => row.Snapshot().PlayingUser == "Owner"));
            row.HandleMessage("Host", true, "!claim @owner", broadcaster: true);
            Assert.False(WaitFor(() => row.Snapshot().PlayingUser != "Owner", 150));
        }

        [Fact]
        public void AViewerWithATranslatedNameIsKnownByTheirLogin()
        {
            using var row = Row();
            const string Shown = "\u5C0F\u660E";
            row.HandleMessage(Shown, false, "!claim", login: "xiaoming");
            // The streamer can name them by the login or by what the page shows.
            row.HandleMessage("Host", true, "!accept @xiaoming", broadcaster: true);
            Assert.Equal(Shown, row.Snapshot().Owner);
            // Someone else with that name shown is someone else.
            row.HandleMessage(Shown, false, "!_a #5s", login: "impostor");
            Assert.Null(row.Snapshot().PlayingUser);
            Assert.Equal(0, row.Snapshot().Waiting);
            row.HandleMessage(Shown, false, "!_a #5s", login: "xiaoming");
            Assert.True(WaitFor(() => row.Snapshot().PlayingUser == Shown));
        }

        [Fact]
        public void AGrantByLoginReachesAViewerWithATranslatedName()
        {
            using var row = Row();
            const string Shown = "\u5C0F\u660E";
            row.HandleMessage("Host", true, "!claim @xiaoming", broadcaster: true);
            Assert.Equal("xiaoming", row.Snapshot().Owner);
            row.HandleMessage(Shown, false, "!_a #5s", login: "xiaoming");
            Assert.True(WaitFor(() => row.Snapshot().PlayingUser == Shown));
        }

        [Fact]
        public void AGrantByTheNameShownReachesItsViewer()
        {
            using var row = Row();
            const string Shown = "\u5C0F\u660E";
            row.HandleMessage("Host", true, "!claim @" + Shown, broadcaster: true);
            row.HandleMessage(Shown, false, "!_a #5s", login: "xiaoming");
            Assert.True(WaitFor(() => row.Snapshot().PlayingUser == Shown));
        }

        [Theory]
        [InlineData("!a \u034F")]               // Chatterino's repeat suffix
        [InlineData("!a \u034F ")]
        [InlineData("!a\u034F")]
        [InlineData("!a \U000E0000")]           // the same idea from the tag block
        [InlineData("!a\u200D")]
        public void MarksThatDrawNothingAtTheEndOfAMessageDoNotBreakIt(string text)
        {
            using var row = Row();
            row.HandleMessage("v", false, text);
            Assert.True(WaitFor(() => row.Snapshot().PlayingUser == "v"));
        }

        [Fact]
        public void ATurnCommandWithChatterinosSuffixIsStillOne()
        {
            using var row = Row();
            row.HandleMessage("Host", true, "!claim @Owner \u034F", broadcaster: true);
            Assert.Equal("Owner", row.Snapshot().Owner);
            row.HandleMessage("Owner", false, "!release \u034F");
            Assert.Null(row.Snapshot().Owner);
        }

        [Theory]
        [InlineData("a", "a")]
        [InlineData("a \u034F", "a")]
        [InlineData("caf\u00E9", "caf\u00E9")]
        [InlineData("a\U0001F600", "a\U0001F600")]   // a picture draws something
        [InlineData("\u034F", "")]
        [InlineData("", "")]
        public void OnlyWhatDrawsNothingIsTrimmed(string text, string trimmed)
            => Assert.Equal(trimmed, TwitchChatDevice.TrimInvisibleEnd(text));

        [Fact]
        public void NothingJoinsTheLineWhilePaused()
        {
            using var player = new ChatCommandPlayer();
            TwitchChatRuntime.Paused = true;
            player.Enqueue(Play("v", "a"));
            Assert.Equal(0, player.WaitingCount);
            Assert.False(player.IsPlaying);
        }

        [Fact]
        public void ASkipOnAClosedPlayerDoesNothing()
        {
            var player = new ChatCommandPlayer();
            player.Dispose();
            Assert.False(player.Skip(all: true));
            player.KeepOnly(_ => false);
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

        // ── A removed word leaves nothing naming its button ──

        private static readonly string Chat = TwitchChatDevice.Identity.ToString();

        private sealed class SavedSettings : IDisposable
        {
            private readonly MappingSet[] _sets = SettingsManager.SlotMappingSets;
            private readonly SettingsCollection _settings = SettingsManager.UserSettings;
            private readonly DeviceCollection _devices = SettingsManager.UserDevices;
            private readonly List<PadForge.Services.ProfileData> _profiles = SettingsManager.Profiles;
            private readonly PadForge.Services.ProfileData _pending = SettingsManager.PendingDefaultSnapshot;
            private readonly PadForge.Services.GlobalMacroData[] _globals = SettingsManager.GlobalMacros;

            public SavedSettings()
            {
                SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
                SettingsManager.UserSettings = new SettingsCollection();
                SettingsManager.UserDevices = new DeviceCollection();
                SettingsManager.Profiles = new List<PadForge.Services.ProfileData>();
                SettingsManager.PendingDefaultSnapshot = null;
                SettingsManager.GlobalMacros = null;
            }

            public void Dispose()
            {
                SettingsManager.SlotMappingSets = _sets;
                SettingsManager.UserSettings = _settings;
                SettingsManager.UserDevices = _devices;
                SettingsManager.Profiles = _profiles;
                SettingsManager.PendingDefaultSnapshot = _pending;
                SettingsManager.GlobalMacros = _globals;
            }
        }

        private static MappingRow RowOf(string target, params MappingSource[] sources)
            => new() { Target = target, Sources = sources.ToList() };

        [Fact]
        public void ANewWordOnAFreedButtonInheritsNoMappingRow()
        {
            using var saved = new SavedSettings();
            var other = Guid.NewGuid().ToString();
            int x = ChatCommandRegistry.ButtonFor("x");
            var ms = new MappingSet();
            ms.Rows.Add(RowOf("ButtonGuide", new MappingSource { DeviceGuid = Chat, Descriptor = "Button " + x }));
            ms.Rows.Add(RowOf("ButtonA",
                new MappingSource { DeviceGuid = Chat, Descriptor = "Button " + x },
                new MappingSource { DeviceGuid = other, Descriptor = "Button " + x }));
            ms.Rows.Add(RowOf("ButtonB", new MappingSource { DeviceGuid = Chat, Descriptor = "Button " + (x + 1) }));
            SettingsManager.SlotMappingSets[0] = ms;

            FreedButtonCleanup.ForChatWord(x, null);

            Assert.DoesNotContain(ms.Rows, r => r.Target == "ButtonGuide");
            // Another controller's Button N is another button.
            var a = Assert.Single(ms.Rows, r => r.Target == "ButtonA");
            Assert.Equal(other, Assert.Single(a.Sources).DeviceGuid);
            Assert.Single(ms.Rows, r => r.Target == "ButtonB");
        }

        [Fact]
        public void TheCleanupFollowsEveryWayARowReadsTheButton()
        {
            using var saved = new SavedSettings();
            const string B3 = "Button 3";
            var ms = new MappingSet();
            ms.Rows.Add(RowOf("InvertedRow", new MappingSource { DeviceGuid = Chat, Descriptor = "I" + B3 }));
            ms.Rows.Add(RowOf("KeptRamp", new MappingSource { Kind = "Ramped", DeviceGuid = Chat, ParamUp = B3, ParamDown = "Button 4" }));
            ms.Rows.Add(RowOf("GoneRamp", new MappingSource { Kind = "Incremental", DeviceGuid = Chat, ParamUp = B3 }));
            ms.Rows.Add(RowOf("GoneModifier", new MappingSource { Kind = "InvertOnHold", DeviceGuid = Chat, ParamModifier = B3 }));
            var custom = RowOf("Custom",
                new MappingSource { DeviceGuid = Chat, Descriptor = B3 },
                new MappingSource { DeviceGuid = Chat, Descriptor = "Button 4" });
            custom.CombineMode = "Custom";
            ms.Rows.Add(custom);
            ms.ShiftActivators.Add(new ShiftActivator { DeviceGuid = Chat, Descriptor = B3, ChordSecondDeviceGuid = Chat, ChordSecondDescriptor = B3, CyclePrevDeviceGuid = Chat, CyclePrevDescriptor = "Button 4" });
            ms.Menus.Add(new PadForge.Engine.Menus.MenuDefinitionEntry { DeviceGuid = Chat, ClickDescriptor = B3 });

            Assert.True(FreedButtonCleanup.ClearSet(ms, (d, n) => d == Chat && (PadForge.Services.SettingsService.StripSourcePrefix(n) == B3)));

            Assert.DoesNotContain(ms.Rows, r => r.Target is "InvertedRow" or "GoneRamp" or "GoneModifier");
            var ramp = Assert.Single(Assert.Single(ms.Rows, r => r.Target == "KeptRamp").Sources);
            Assert.Equal("", ramp.ParamUp);
            Assert.Equal("Button 4", ramp.ParamDown);
            // A custom row's sources are its expression's arguments, so the
            // place stays.
            var args = Assert.Single(ms.Rows, r => r.Target == "Custom").Sources;
            Assert.Equal(new[] { "", "Button 4" }, args.Select(s => s.Descriptor));
            Assert.Equal("", ms.ShiftActivators[0].Descriptor);
            Assert.Equal("", ms.ShiftActivators[0].ChordSecondDescriptor);
            Assert.Equal("Button 4", ms.ShiftActivators[0].CyclePrevDescriptor);
            Assert.Equal("", ms.Menus[0].ClickDescriptor);
        }

        [Fact]
        public void TheLegacyPadSettingLosesTheButton_SoAMergeCannotBringTheRowBack()
        {
            using var saved = new SavedSettings();
            int x = ChatCommandRegistry.ButtonFor("x");
            var ps = new PadSetting { ButtonX = "Button " + x, ButtonY = "Button " + (x + 1) };
            ps.SetRawMapping("RawBtn2", "Button " + x);
            ps.SetRawMapping("RawBtn3", "Button " + (x + 1));
            ps.FlushRawMappings();
            ps.UpdateChecksum();
            var us = new UserSetting { InstanceGuid = TwitchChatDevice.Identity, MapTo = 0, PadSettingChecksum = ps.PadSettingChecksum };
            us.SetPadSetting(ps);
            var bystander = new UserSetting { InstanceGuid = Guid.NewGuid(), MapTo = 1 };
            var theirs = new PadSetting { ButtonX = "Button " + x };
            bystander.SetPadSetting(theirs);
            lock (SettingsManager.UserSettings.SyncRoot)
            {
                SettingsManager.UserSettings.Items.Add(us);
                SettingsManager.UserSettings.Items.Add(bystander);
            }

            FreedButtonCleanup.ForChatWord(x, null);

            var cleared = us.GetPadSetting();
            Assert.Equal("", cleared.ButtonX);
            Assert.Equal("Button " + (x + 1), cleared.ButtonY);
            Assert.Equal("", cleared.GetRawMapping("RawBtn2"));
            Assert.Equal("Button " + (x + 1), cleared.GetRawMapping("RawBtn3"));
            Assert.Equal(cleared.PadSettingChecksum, us.PadSettingChecksum);
            Assert.NotEqual(ps.PadSettingChecksum, us.PadSettingChecksum);
            Assert.Same(theirs, bystander.GetPadSetting());
            Assert.Equal("Button " + x, theirs.ButtonX);
        }

        /// <summary>MIDI, keyboard and mouse, and VR rows are rebuilt from
        /// these dictionaries as raw rows are from theirs.</summary>
        [Fact]
        public void TheOutputSurfaceMappings_LoseTheButton_SoAMergeCannotBringTheRowBack()
        {
            var ps = new PadSetting();
            ps.SetMidiMapping("MidiNote3", "Button 3");
            ps.SetMidiMapping("MidiNote4", "Button 4");
            ps.FlushMidiMappings();
            ps.SetKbmMapping("KbmKey41", "Button 3");
            ps.SetKbmMapping("KbmKey42", "Button 4");
            ps.FlushKbmMappings();
            ps.SetVrMapping("VrRA", "IButton 3");
            ps.SetVrMapping("VrRB", "Button 4");
            ps.FlushVrMappings();
            ps.UpdateChecksum();
            string before = ps.PadSettingChecksum;

            var cleared = FreedButtonCleanup.WithoutButton(ps, Chat,
                (d, n) => d == Chat && PadForge.Services.SettingsService.StripSourcePrefix(n) == "Button 3");

            Assert.NotNull(cleared);
            Assert.Equal("MidiNote4", Assert.Single(cleared.MidiMappingEntries).Key);
            Assert.Equal("KbmKey42", Assert.Single(cleared.KbmMappingEntries).Key);
            Assert.Equal("VrRB", Assert.Single(cleared.VrMappingEntries).Key);
            Assert.Equal("", cleared.GetMidiMapping("MidiNote3"));
            Assert.NotEqual(before, cleared.PadSettingChecksum);
            // The original may be shared by checksum.
            Assert.Equal("Button 3", ps.GetMidiMapping("MidiNote3"));
        }

        /// <summary>An engage button that names a controller is that
        /// controller's Button N, whichever row's PadSetting holds it. With no
        /// controller named, it reads on the row that holds it.</summary>
        [Fact]
        public void AnEngagePinOnAnotherController_Stays_WhenTheRowItSitsOnLosesThatNumber()
        {
            string other = Guid.NewGuid().ToString();
            var ps = new PadSetting
            {
                GyroAimEngageDeviceGuid = other, GyroAimEngageButton = "Button 3",
                LeftTriggerRouteActivatorDeviceGuid = other, LeftTriggerRouteActivator = "Button 3",
                RightTriggerRouteActivatorDeviceGuid = other, RightTriggerRouteActivator = "Button 3",
            };
            FreedButtonCleanup.NamesButton names = (d, n) => d == Chat && n == "Button 3";
            Assert.Null(FreedButtonCleanup.WithoutButton(ps, Chat, names));

            ps.GyroAimEngageDeviceGuid = "";
            Assert.Equal("", FreedButtonCleanup.WithoutButton(ps, Chat, names).GyroAimEngageButton);
        }

        [Fact]
        public void AnEngagePinOnTheButtonClears_WhateverRowItSitsOn()
        {
            var ps = new PadSetting { GyroAimEngageDeviceGuid = Chat, GyroAimEngageButton = "Button 3", RightTriggerRouteActivatorDeviceGuid = Chat, RightTriggerRouteActivator = "Button 3" };
            var cleared = FreedButtonCleanup.WithoutButton(ps, Guid.NewGuid().ToString(), (d, n) => d == Chat && n == "Button 3");
            Assert.Equal("", cleared.GyroAimEngageButton);
            Assert.Equal("", cleared.RightTriggerRouteActivator);
        }

        [Fact]
        public void ASavedProfileLosesTheButtonToo()
        {
            using var saved = new SavedSettings();
            int x = ChatCommandRegistry.ButtonFor("x");
            var pinned = new PadSetting { ButtonX = "Button " + x };
            pinned.UpdateChecksum();
            string oldSum = pinned.PadSettingChecksum;
            var ms = new MappingSet();
            ms.Rows.Add(RowOf("ButtonX", new MappingSource { DeviceGuid = Chat, Descriptor = "Button " + x }));
            string entry = $"in:{Chat}:btn:{x}";
            string keep = $"in:{Guid.NewGuid()}:btn:{x}";
            var profile = new PadForge.Services.ProfileData
            {
                SlotMappingSets = new[] { ms },
                PadSettings = new[] { pinned },
                Entries = new[] { new PadForge.Services.ProfileEntry { InstanceGuid = TwitchChatDevice.Identity, MapTo = 0, PadSettingChecksum = oldSum } },
                Macros = new[]
                {
                    new PadForge.Services.MacroData { TriggerInputs = entry + "|" + keep, TriggerRawButtons = "1", TriggerExpressionVariables = entry + "|" + keep },
                    new PadForge.Services.MacroData { TriggerInputs = entry, TriggerRawButtons = "1", TriggerPovs = new[] { "0:0" } },
                },
            };
            SettingsManager.Profiles.Add(profile);

            FreedButtonCleanup.ForChatWord(x, null);

            Assert.Empty(ms.Rows);
            var newSum = profile.Entries[0].PadSettingChecksum;
            Assert.NotEqual(oldSum, newSum);
            Assert.Equal("", profile.PadSettings.Single(p => p.PadSettingChecksum == newSum).ButtonX);
            Assert.Equal(keep, profile.Macros[0].TriggerInputs);
            // The variable keeps its letter.
            Assert.Equal("|" + keep, profile.Macros[0].TriggerExpressionVariables);
            // The legacy fields would come back when the spec is empty.
            Assert.Equal("", profile.Macros[1].TriggerInputs);
            Assert.Null(profile.Macros[1].TriggerRawButtons);
            Assert.Null(profile.Macros[1].TriggerPovs);
        }

        [Fact]
        public void TheDefaultProfilesPendingSnapshotAndAGlobalMacroLoseTheButton()
        {
            using var saved = new SavedSettings();
            int x = ChatCommandRegistry.ButtonFor("x");
            var ms = new MappingSet();
            ms.Rows.Add(RowOf("ButtonX", new MappingSource { DeviceGuid = Chat, Descriptor = "Button " + x }));
            SettingsManager.PendingDefaultSnapshot = new PadForge.Services.ProfileData { SlotMappingSets = new[] { ms } };
            SettingsManager.GlobalMacros = new[]
            {
                new PadForge.Services.GlobalMacroData
                {
                    TriggerEntries = new[]
                    {
                        new PadForge.Services.TriggerButtonEntry { DeviceInstanceGuid = TwitchChatDevice.Identity, ButtonIndex = x },
                        new PadForge.Services.TriggerButtonEntry { DeviceInstanceGuid = TwitchChatDevice.Identity, ButtonIndex = x + 1 },
                    },
                },
            };

            FreedButtonCleanup.ForChatWord(x, null);

            Assert.Empty(ms.Rows);
            Assert.Equal(x + 1, Assert.Single(SettingsManager.GlobalMacros[0].TriggerEntries).ButtonIndex);
        }

        [Fact]
        public void AMacroOnThePadLosesTheTrigger_AndTheGridFollowsTheSet()
        {
            using var saved = new SavedSettings();
            int x = ChatCommandRegistry.ButtonFor("x");
            var pad = new PadViewModel(0);
            var macro = new MacroItem();
            macro.SetTriggerInputEntries(new List<MacroItem.TriggerInputEntry>
            {
                new() { DeviceGuid = TwitchChatDevice.Identity, RawButton = x },
                new() { DeviceGuid = TwitchChatDevice.Identity, RawButton = x + 1 },
            });
            var variable = new MacroExpressionVariable { DeviceGuid = TwitchChatDevice.Identity, RawButton = x };
            macro.TriggerExpressionVariables.Add(variable);
            pad.Macros.Add(macro);
            pad.GyroAimEngageDeviceGuid = Chat;
            pad.GyroAimEngageButton = "Button " + x;
            var ms = new MappingSet();
            ms.Rows.Add(RowOf("ButtonA", new MappingSource { DeviceGuid = Chat, Descriptor = "Button " + x }));
            SettingsManager.SlotMappingSets[0] = ms;
            InputService.RefreshMappingsToViewModel(pad);
            var row = pad.Mappings.Single(m => m.TargetSettingName == "ButtonA");
            Assert.Equal("Button " + x, row.SourceDescriptor);

            FreedButtonCleanup.ForChatWord(x, new[] { pad });

            Assert.Equal(x + 1, Assert.Single(macro.GetTriggerInputEntries()).RawButton);
            Assert.Equal(-1, variable.RawButton);
            Assert.Equal("", pad.GyroAimEngageButton);
            Assert.True(string.IsNullOrEmpty(pad.Mappings.Single(m => m.TargetSettingName == "ButtonA").SourceDescriptor));
        }

        [Fact]
        public void AVoicePhrasesPadDescriptorAndMicrophoneRowButtonBothClear_AndAPadsOwnButtonStays()
        {
            using var saved = new SavedSettings();
            var mic = new UserDevice { InstanceGuid = Guid.NewGuid(), CapType = InputDeviceType.Microphone };
            lock (SettingsManager.UserDevices.SyncRoot) SettingsManager.UserDevices.Items.Add(mic);
            var gamepad = Guid.NewGuid().ToString();
            var ms = new MappingSet();
            ms.Rows.Add(RowOf("A", new MappingSource { DeviceGuid = gamepad, Descriptor = "Voice Phrase 2" }));
            ms.Rows.Add(RowOf("B", new MappingSource { DeviceGuid = mic.InstanceGuid.ToString(), Descriptor = "Button 2" }));
            ms.Rows.Add(RowOf("C", new MappingSource { DeviceGuid = gamepad, Descriptor = "Button 2" }));
            ms.Rows.Add(RowOf("D", new MappingSource { DeviceGuid = gamepad, Descriptor = "Voice Phrase 3" }));
            SettingsManager.SlotMappingSets[0] = ms;

            FreedButtonCleanup.ForVoicePhrase(2, null);

            Assert.Equal(new[] { "C", "D" }, ms.Rows.Select(r => r.Target));
        }

        /// <summary>A variable speaks raw buttons, so on a pad it holds the
        /// place the phrase's pulse is stamped at. The same number on a
        /// keyboard's row is a key.</summary>
        [Fact]
        public void AVariableOnAPhrasesPlaceOnAPad_Clears_AndAKeyOfThatNumberStays()
        {
            using var saved = new SavedSettings();
            int place = PadForge.Engine.Common.Mapping.SourceCoercion.VoicePhraseButtonBase + 19;
            var dualSense = new UserDevice { InstanceGuid = Guid.NewGuid(), CapType = InputDeviceType.Gamepad, VendorId = 0x054C, ProdId = 0x0CE6 };
            var keyboard = new UserDevice { InstanceGuid = Guid.NewGuid(), CapType = InputDeviceType.Keyboard };
            lock (SettingsManager.UserDevices.SyncRoot)
            {
                SettingsManager.UserDevices.Items.Add(dualSense);
                SettingsManager.UserDevices.Items.Add(keyboard);
            }
            string onPad = $"in:{dualSense.InstanceGuid}:btn:{place}";
            string onKey = $"in:{keyboard.InstanceGuid}:btn:{place}";
            string nextPhrase = $"in:{dualSense.InstanceGuid}:btn:{place + 1}";
            var profile = new PadForge.Services.ProfileData
            {
                Macros = new[] { new PadForge.Services.MacroData { TriggerExpressionVariables = onPad + "|" + onKey + "|" + nextPhrase } },
            };
            SettingsManager.Profiles.Add(profile);
            var pad = new PadViewModel(0);
            var macro = new MacroItem();
            var padVariable = new MacroExpressionVariable { DeviceGuid = dualSense.InstanceGuid, RawButton = place };
            var keyVariable = new MacroExpressionVariable { DeviceGuid = keyboard.InstanceGuid, RawButton = place };
            macro.TriggerExpressionVariables.Add(padVariable);
            macro.TriggerExpressionVariables.Add(keyVariable);
            pad.Macros.Add(macro);

            FreedButtonCleanup.ForVoicePhrase(19, new[] { pad });

            Assert.Equal("|" + onKey + "|" + nextPhrase, profile.Macros[0].TriggerExpressionVariables);
            Assert.Equal(-1, padVariable.RawButton);
            Assert.Equal(place, keyVariable.RawButton);
        }

        [Fact]
        public void AnNfcTagsPadDescriptorAndReaderRowButtonBothClear()
        {
            using var saved = new SavedSettings();
            var reader = new UserDevice { InstanceGuid = Guid.NewGuid(), CapType = InputDeviceType.Nfc };
            lock (SettingsManager.UserDevices.SyncRoot) SettingsManager.UserDevices.Items.Add(reader);
            var ms = new MappingSet();
            ms.Rows.Add(RowOf("A", new MappingSource { DeviceGuid = Guid.NewGuid().ToString(), Descriptor = "NFC Tag 2" }));
            ms.Rows.Add(RowOf("B", new MappingSource { DeviceGuid = reader.InstanceGuid.ToString(), Descriptor = "Button 2" }));
            ms.Rows.Add(RowOf("C", new MappingSource { DeviceGuid = Guid.NewGuid().ToString(), Descriptor = "NFC Tag 3" }));
            SettingsManager.SlotMappingSets[0] = ms;
            FreedButtonCleanup.ForNfcTag(2, null);
            Assert.Equal(new[] { "C" }, ms.Rows.Select(r => r.Target));
        }

        [Fact]
        public void AHandheldButtonsRowClears()
        {
            using var saved = new SavedSettings();
            var row = new UserDevice { InstanceGuid = Guid.NewGuid(), CapType = InputDeviceType.HandheldButtons };
            lock (SettingsManager.UserDevices.SyncRoot) SettingsManager.UserDevices.Items.Add(row);
            var ms = new MappingSet();
            ms.Rows.Add(RowOf("A", new MappingSource { DeviceGuid = row.InstanceGuid.ToString(), Descriptor = "Button 2" }));
            SettingsManager.SlotMappingSets[0] = ms;
            FreedButtonCleanup.ForHandheldButton(2, null);
            Assert.Empty(ms.Rows);
        }

        [Fact]
        public void EachRegistryNamesTheButtonItFreed_AndInputServiceListens()
        {
            var chat = new List<int>();
            Action<int> onChat = chat.Add;
            ChatCommandRegistry.ButtonFreed += onChat;
            try
            {
                int x = ChatCommandRegistry.ButtonFor("x");
                ChatCommandRegistry.Remove("x");
                ChatCommandRegistry.Remove("x");
                Assert.Equal(new[] { x }, chat);
            }
            finally { ChatCommandRegistry.ButtonFreed -= onChat; }

            var phrases = new List<int>();
            Action<int> onPhrase = phrases.Add;
            VoicePhraseRegistry.LoadRegistry(new[] { ("reload", "Reload", 7) });
            VoicePhraseRegistry.ButtonFreed += onPhrase;
            try
            {
                VoicePhraseRegistry.Remove("reload");
                Assert.Equal(new[] { 7 }, phrases);
            }
            finally
            {
                VoicePhraseRegistry.ButtonFreed -= onPhrase;
                VoicePhraseRegistry.LoadRegistry(null);
            }

            var tags = new List<int>();
            Action<int> onTag = tags.Add;
            NfcTagRegistry.LoadRegistry(new[] { ("04AABBCC", "Tag", 5) });
            NfcTagRegistry.ButtonFreed += onTag;
            try
            {
                NfcTagRegistry.Remove("04AABBCC");
                Assert.Equal(new[] { 5 }, tags);
            }
            finally
            {
                NfcTagRegistry.ButtonFreed -= onTag;
                NfcTagRegistry.LoadRegistry(null);
            }

            var learned = new List<int>();
            Action<int> onLearned = learned.Add;
            string machine = HandheldButtonRegistry.MachineKey;
            HandheldButtonRegistry.LoadRegistry(new[] { new HandheldButtonRegistry.Entry { Name = "Back", Button = 4, Keys = new[] { 1 } } }, machine);
            HandheldButtonRegistry.ButtonFreed += onLearned;
            try
            {
                HandheldButtonRegistry.Remove(4);
                Assert.Equal(new[] { 4 }, learned);
            }
            finally
            {
                HandheldButtonRegistry.ButtonFreed -= onLearned;
                HandheldButtonRegistry.LoadRegistry(null, machine);
            }

            string wiring = File.ReadAllText(Path.Combine(RepoRoot(), "PadForge.App", "Services", "InputService.cs"));
            foreach (string line in new[]
                     {
                         "ChatCommandRegistry.ButtonFreed += OnChatWordFreed;", "VoicePhraseRegistry.ButtonFreed += OnVoicePhraseFreed;",
                         "NfcTagRegistry.ButtonFreed += OnNfcTagFreed;", "HandheldButtonRegistry.ButtonFreed += OnHandheldButtonFreed;",
                     })
                Assert.Contains(line, wiring);
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
            Assert.Equal(string.Format(s.TwitchChat_Status_NoChat_Format, "chan"), InputService.BuildTwitchChatStatus("chan", TwitchChatState.NoChat, false));
            Assert.Contains("chan", s.TwitchChat_Status_NoChat_Format.Replace("{0}", "chan"));
        }

        [Fact]
        public void ThePicturePageIsToldWhenTwitchHasNoChat()
        {
            var snapshot = new TwitchChatSnapshot("chan", TwitchChatState.NoChat, false, null, null,
                new List<(string, string)>(), 0, null, 0, new List<(string, int, long)>(), 0, "");
            Assert.Contains("\"state\":\"nochat\"", System.Text.Json.JsonSerializer.Serialize(WebControllerServer.ChatMessage(snapshot)));
            Assert.Contains("c.state === \"nochat\"", Web(Path.Combine("js", "display_client.js")));
        }

        [Theory]
        [InlineData("DS3", "ds4")]
        [InlineData(null, "xbox360")]
        [InlineData("STEAMCONTROLLER2", "steamcontroller2")]
        [InlineData("STEAMCONTROLLER", "steamcontroller")]
        [InlineData("STEAMDECK", "steamdeck")]
        [InlineData("DUALSENSEEDGE", "dualsenseedge")]
        [InlineData("DualSense", "dualsense")]
        [InlineData("DS4", "ds4")]
        [InlineData("XBOXSERIES", "xboxseries")]
        [InlineData("XBOXONE", "xboxone")]
        [InlineData("XBOX360", "xbox360")]
        [InlineData("SWITCH2PRO", "switch2pro")]
        [InlineData("SWITCHPRO", "switchpro")]
        public void EachControllerIsDrawnWithItsOwnLayout(string folder, string layout)
            => Assert.Equal(layout, InputService.WebLayoutFor(folder));

        // ── The web controller ──

        [Theory]
        [InlineData(null, null)]
        [InlineData("   ", null)]
        [InlineData(" Alex ", "Alex")]
        [InlineData("A\u202Elex\u0007", "Alex")]
        [InlineData("abcdefghijklmnopqrstuvwxyz", "abcdefghijklmnopqrstuvwx")]
        public void AViewersNameIsCleanedForTheDeviceRow(string raw, string name)
            => Assert.Equal(name, WebControllerServer.CleanViewerName(raw));

        private static void WithSlots(Action body, IEnumerable<int> xbox, IEnumerable<int> playStation)
        {
            var created = (bool[])SettingsManager.SlotCreated.Clone();
            var (x, ps) = (SettingsManager.XboxSlotOrder, SettingsManager.PlayStationSlotOrder);
            var (n, e, k, m, v) = (SettingsManager.NintendoSlotOrder, SettingsManager.ExtendedSlotOrder,
                SettingsManager.KeyboardMouseSlotOrder, SettingsManager.MidiSlotOrder, SettingsManager.VrSlotOrder);
            try
            {
                SettingsManager.XboxSlotOrder = xbox.ToList();
                SettingsManager.PlayStationSlotOrder = playStation.ToList();
                SettingsManager.NintendoSlotOrder = new List<int>();
                SettingsManager.ExtendedSlotOrder = new List<int>();
                SettingsManager.KeyboardMouseSlotOrder = new List<int>();
                SettingsManager.MidiSlotOrder = new List<int>();
                SettingsManager.VrSlotOrder = new List<int>();
                Array.Clear(SettingsManager.SlotCreated);
                foreach (int i in SettingsManager.XboxSlotOrder.Concat(SettingsManager.PlayStationSlotOrder))
                    SettingsManager.SlotCreated[i] = true;
                body();
            }
            finally
            {
                Array.Copy(created, SettingsManager.SlotCreated, created.Length);
                SettingsManager.XboxSlotOrder = x;
                SettingsManager.PlayStationSlotOrder = ps;
                SettingsManager.NintendoSlotOrder = n;
                SettingsManager.ExtendedSlotOrder = e;
                SettingsManager.KeyboardMouseSlotOrder = k;
                SettingsManager.MidiSlotOrder = m;
                SettingsManager.VrSlotOrder = v;
            }
        }

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

        private static WebControllerServer ServerWithSlotNumbers()
            => new(_ => null, () => null) { DisplayNamedSlot = SettingsManager.SlotOrders.PadIndexForGlobalSlotNumber };

        [Fact]
        public void InputServiceGivesTheServerThePadForgeNumbers()
        {
            string wiring = File.ReadAllText(Path.Combine(RepoRoot(), "PadForge.App", "Services", "InputService.cs"));
            Assert.Contains("_webServer.DisplayNamedSlot = SettingsManager.SlotOrders.PadIndexForGlobalSlotNumber;", wiring);
            Assert.Contains("_webServer.DisplayShownNumber = ShownSlotNumber;", wiring);
        }

        [Fact]
        public void TheSlotMessageSaysTheNumberPadForgeShows()
            => WithSlots(() =>
            {
                Assert.Equal(1, InputService.ShownSlotNumber(1));
                Assert.Equal(2, InputService.ShownSlotNumber(0));
                // In no order list: its index + 1.
                SettingsManager.SlotCreated[6] = true;
                Assert.Equal(7, InputService.ShownSlotNumber(6));
            }, new[] { 1 }, new[] { 0 });

        [Fact]
        public void ANamedSlotIsTheNumberPadForgeShows_NotThePadIndex()
            => WithSlots(() =>
            {
                using var server = ServerWithSlotNumbers();
                // Xbox slots are numbered before PlayStation ones.
                Assert.Equal(1, server.DisplaySlotFor("1"));
                Assert.Equal(0, server.DisplaySlotFor("2"));
                Assert.Equal(1, server.DisplaySlotFor("auto"));
            }, new[] { 1 }, new[] { 0 });

        [Fact]
        public void ANumberNoSlotShowsFallsBack_AndADeletedSlotLeavesNoHoleInTheNumbers()
            => WithSlots(() =>
            {
                using var server = ServerWithSlotNumbers();
                Assert.Equal(0, server.DisplaySlotFor("1"));
                Assert.Equal(2, server.DisplaySlotFor("2"));
                Assert.Equal(0, server.DisplaySlotFor("3"));
                server.DisplayChatSlot = () => 2;
                Assert.Equal(2, server.DisplaySlotFor("3"));
            }, new[] { 0, 2 }, Array.Empty<int>());

        [Fact]
        public void TheNumberAndItsSlotAreInverses()
            => WithSlots(() =>
            {
                foreach (int pad in new[] { 4, 1, 7, 2 })
                    Assert.Equal(pad, SettingsManager.SlotOrders.PadIndexForGlobalSlotNumber(SettingsManager.SlotOrders.GetGlobalSlotNumber(pad)));
                Assert.Equal(-1, SettingsManager.SlotOrders.PadIndexForGlobalSlotNumber(5));
                Assert.Equal(-1, SettingsManager.SlotOrders.PadIndexForGlobalSlotNumber(0));
            }, new[] { 4, 1 }, new[] { 7, 2 });

        [Fact]
        public void ACreatedSlotInNoOrderListKeepsItsIndexPlusOne()
            => WithSlots(() =>
            {
                SettingsManager.SlotCreated[3] = true;
                Assert.Equal(0, SettingsManager.SlotOrders.GetGlobalSlotNumber(3));
                Assert.Equal(3, SettingsManager.SlotOrders.PadIndexForGlobalSlotNumber(4));
                Assert.Equal(-1, SettingsManager.SlotOrders.PadIndexForGlobalSlotNumber(3));
            }, Array.Empty<int>(), Array.Empty<int>());

        // ── Names the settings file cannot hold ──

        private static string Saved(object data)
        {
            var serializer = new System.Xml.Serialization.XmlSerializer(data.GetType());
            return System.Text.Encoding.UTF8.GetString(PadForge.Engine.Common.XmlSafeText.Serialize(serializer, data));
        }

        // The characters go in as numbers: the test runner's own serializer
        // turns U+FFFE in an attribute's string into something else.
        [Theory]
        [InlineData(0xFFFE)]
        [InlineData(0xFFFF)]
        [InlineData(0x0001)]
        [InlineData(0xD800)]
        public void AViewersNameKeepsOnlyWhatTheSettingsFileCanHold(int bad)
            => Assert.Equal("Alex", WebControllerServer.CleanViewerName("Al" + (char)bad + "ex"));

        [Theory]
        [InlineData(0xFFFE)]
        [InlineData(0xFFFF)]
        [InlineData(0x0001)]
        [InlineData(0xDC00)]
        [InlineData(0xD800)]
        public void WhatXmlCannotHoldIsCleaned(int bad)
        {
            Assert.Equal("Pad", PadForge.Engine.Common.XmlSafeText.Clean("Pad" + (char)bad));
            Assert.Equal("Padx", PadForge.Engine.Common.XmlSafeText.Clean("Pad" + (char)bad + "x"));
        }

        [Theory]
        [InlineData("Pad \U0001F600")]
        [InlineData("Plain")]
        [InlineData("\t\n")]
        [InlineData("\uFFFD")]
        [InlineData("")]
        public void WhatXmlCanHoldIsLeftAlone(string text)
            => Assert.Same(text, PadForge.Engine.Common.XmlSafeText.Clean(text));

        [Fact]
        public void ACustomPadsNameKeepsOnlyWhatTheSettingsFileCanHold()
        {
            string before = WebCustomLayoutStore.Json;
            try
            {
                string id = WebCustomLayoutStore.Upsert(
                    "{\"name\":\"My\\uFFFE Pad\\u0001\",\"widgets\":[{\"kind\":\"button\",\"x\":0,\"y\":0,\"w\":0.1,\"h\":0.1,\"code\":0}]}");
                Assert.NotNull(id);
                using var doc = System.Text.Json.JsonDocument.Parse(WebCustomLayoutStore.Find(id));
                Assert.Equal("My Pad", doc.RootElement.GetProperty("name").GetString());
            }
            finally
            {
                WebCustomLayoutStore.LoadFrom(before);
            }
        }

        [Fact]
        public void AWebDeviceBuiltFromAnythingABrowserSendsCanBeSaved()
        {
            var device = new WebControllerDevice("xbox360:id\u0001\uFFFE", "Pad: \uFFFFName");
            Assert.Equal("web://xbox360:id", device.DevicePath);
            Assert.Equal("Pad: Name", device.Name);
            var ud = new UserDevice();
            ud.LoadFromWebDevice(device);
            var data = new PadForge.Services.SettingsFileData { Devices = new[] { ud } };
            Assert.Contains("web://xbox360:id", Saved(data), StringComparison.Ordinal);
        }

        [Fact]
        public void ADeviceNameFromAnySourceCostsItsBadCharacters_NotTheSave()
        {
            var ud = new UserDevice { ProductName = "Bad\uFFFE Name", InstanceName = "Bad\u0001 Name", SerialNumber = "\uFFFF1" };
            var data = new PadForge.Services.SettingsFileData { Devices = new[] { ud } };
            string xml = Saved(data);
            Assert.Contains("Bad Name", xml, StringComparison.Ordinal);
            // And what was written reads back.
            Assert.Equal("1", System.Xml.Linq.XDocument.Parse(xml).Root.Element("Devices")?.Elements().First().Element("SerialNumber")?.Value);
        }

        [Fact]
        public void AProfileExportHoldsOnlyWhatXmlCanHold()
        {
            string dir = Path.Combine(Path.GetTempPath(), "pf-export-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string path = Path.Combine(dir, "p.zip");
                ProfileTransfer.Export(new PadForge.Services.ProfileData { Name = "Bad\uFFFE Name" }, path);
                Assert.True(File.Exists(path));
            }
            finally
            {
                foreach (var f in Directory.GetFiles(dir)) File.Delete(f);
                Directory.Delete(dir);
            }
        }

        [Fact]
        public void AnAttributesValueIsCleanedToo()
        {
            var ms = new MappingSet();
            ms.Rows.Add(RowOf("ButtonA", new MappingSource { Descriptor = "Bad\uFFFE" }));
            string xml = Saved(new PadForge.Services.ProfileData { SlotMappingSets = new[] { ms } });
            Assert.Contains("Descriptor=\"Bad\"", xml, StringComparison.Ordinal);
        }

        [Fact]
        public void TheSettingsFileIsWrittenThroughTheCleaningWriter()
            => Assert.Contains("XmlSafeText.Serialize(serializer, data)",
                File.ReadAllText(Path.Combine(RepoRoot(), "PadForge.App", "Services", "SettingsService.cs")));

        [Fact]
        public void ASaveOfCleanTextIsByteForByteWhatTheSerializerWrites()
        {
            var ud = new UserDevice { ProductName = "Pad \U0001F600 & <co>", InstanceName = "A\tB" };
            var data = new PadForge.Services.SettingsFileData { Devices = new[] { ud } };
            var serializer = new System.Xml.Serialization.XmlSerializer(data.GetType());
            using var plain = new MemoryStream();
            serializer.Serialize(plain, data);
            Assert.Equal(plain.ToArray(), PadForge.Engine.Common.XmlSafeText.Serialize(serializer, data));
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
        public void ThePicturePageFitsItsDrawingToItsBoxWheneverTheBoxChanges_AndCutsLongCommands()
        {
            string page = Web(Path.Combine("js", "display_client.js"));
            Assert.Contains("new ResizeObserver(refit).observe(padBox)", page);
            Assert.Contains("text: \"!\" + shown(c.text)", page);
            Assert.Contains("text: \"!\" + shown(n.text)", page);
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
