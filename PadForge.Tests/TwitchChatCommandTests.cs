using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input.Twitch;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Issue #555: the Twitch Chat row's word list, Yuriy's command grammar
    /// from ForgePlay (CommandParser.js, CommandExecutor.js) and his rules for
    /// taking turns (ClaimControl.js, SkipControl.js).
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public sealed class TwitchChatCommandTests : IDisposable
    {
        public TwitchChatCommandTests()
        {
            ChatCommandRegistry.LoadRegistry(null);
            TwitchChatRuntime.ResetForTest();
        }

        public void Dispose()
        {
            ChatCommandRegistry.LoadRegistry(null);
            TwitchChatRuntime.ResetForTest();
        }

        private static int B(string word) => ChatCommandRegistry.ButtonFor(word);

        private static IReadOnlyList<ChatStep> P(string text) => ChatCommandParser.Parse(text, ChatCommandRegistry.ButtonFor);

        private static List<ChatEvent> T(string text, out int end) => ChatCommandParser.Timeline(P(text), out end);

        // ── The word list ──

        [Fact]
        public void AFileThatNeverHeldTheList_RegistersTheUsualWordsAtButtonsOneToSixteen()
        {
            var words = ChatCommandRegistry.Words;
            Assert.Equal(ChatCommandRegistry.UsualWords, words.Select(w => w.Word));
            Assert.Equal(Enumerable.Range(1, 16), words.Select(w => w.Button));
            Assert.Equal(16, ChatCommandRegistry.MaxButtonInUse);
        }

        [Fact]
        public void TheUsualWordsLeaveOutHomeAndTheTouchpad()
        {
            Assert.DoesNotContain("home", ChatCommandRegistry.UsualWords);
            Assert.DoesNotContain("guide", ChatCommandRegistry.UsualWords);
            Assert.DoesNotContain("touchpad", ChatCommandRegistry.UsualWords);
        }

        [Fact]
        public void AnEmptySavedListStaysEmpty()
        {
            ChatCommandRegistry.LoadRegistry(Array.Empty<(string, int)>());
            Assert.Equal(0, ChatCommandRegistry.Count);
            Assert.Equal(0, ChatCommandRegistry.MaxButtonInUse);
        }

        [Fact]
        public void ARemovedWordLeavesAGap_AndANewWordTakesTheLowestFreeButton()
        {
            ChatCommandRegistry.Remove("x");
            Assert.Equal(-1, B("x"));
            Assert.Equal(4, B("y"));
            Assert.Equal(3, ChatCommandRegistry.Add("!Jump"));
            Assert.Equal(3, B("jump"));
            Assert.Equal(17, ChatCommandRegistry.Add("dash"));
            // A word already in the list keeps its button.
            Assert.Equal(3, ChatCommandRegistry.Add("JUMP"));
            Assert.Equal(17, ChatCommandRegistry.Count);
        }

        [Theory]
        [InlineData("", ChatCommandRegistry.WordProblem.Empty)]
        [InlineData("!", ChatCommandRegistry.WordProblem.Empty)]
        [InlineData("abcdefghijklmnopqrstu", ChatCommandRegistry.WordProblem.TooLong)]
        [InlineData("2jump", ChatCommandRegistry.WordProblem.Characters)]
        [InlineData("ju-mp", ChatCommandRegistry.WordProblem.Characters)]
        [InlineData("jump2s", ChatCommandRegistry.WordProblem.EndsInTime)]
        [InlineData("a500ms", ChatCommandRegistry.WordProblem.EndsInTime)]
        [InlineData("skipall", ChatCommandRegistry.WordProblem.Reserved)]
        [InlineData("!Claim", ChatCommandRegistry.WordProblem.Reserved)]
        [InlineData("jump", ChatCommandRegistry.WordProblem.None)]
        [InlineData("jump2", ChatCommandRegistry.WordProblem.None)]
        [InlineData("bus", ChatCommandRegistry.WordProblem.None)]
        [InlineData("abcdefghijklmnopqrst", ChatCommandRegistry.WordProblem.None)]
        public void AWordIsLettersAndDigits_StartingWithALetter_NotEndingInATime_NotReserved(string word, ChatCommandRegistry.WordProblem problem)
            => Assert.Equal(problem, ChatCommandRegistry.Check(word));

        [Fact]
        public void ASavedListKeepsItsButtons_AndARepairedEntryTakesTheLowestFreeOne()
        {
            ChatCommandRegistry.LoadRegistry(new[] { ("jump", 5), ("dash", 5), ("bad word", 2), ("roll", 300), ("JUMP", 7) });
            Assert.Equal(5, B("jump"));
            Assert.Equal(1, B("dash"));
            Assert.Equal(-1, B("bad word"));
            Assert.Equal(2, B("roll"));
            Assert.Equal(3, ChatCommandRegistry.Count);
        }

        [Fact]
        public void RestoreUsualWords_AddsOnlyTheMissingOnes_AndKeepsEveryButton()
        {
            ChatCommandRegistry.LoadRegistry(new[] { ("a", 9), ("jump", 1) });
            ChatCommandRegistry.RestoreUsualWords();
            Assert.Equal(9, B("a"));
            Assert.Equal(1, B("jump"));
            Assert.Equal(2, B("b"));
            Assert.Equal(10, B("select"));
            Assert.Equal(17, ChatCommandRegistry.Count);
        }

        [Fact]
        public void TheListRoundTripsThroughTheSettings()
        {
            ChatCommandRegistry.Remove("lb");
            ChatCommandRegistry.Add("jump");
            var saved = ChatCommandRegistry.SaveRegistry();
            ChatCommandRegistry.LoadRegistry(null);
            ChatCommandRegistry.LoadRegistry(saved);
            Assert.Equal(saved.OrderBy(s => s.Button), ChatCommandRegistry.SaveRegistry().OrderBy(s => s.Button));
            Assert.Equal(5, B("jump"));
        }

        [Fact]
        public void AChangeRaisesRegistryChanged_AndBumpsTheVersion()
        {
            int raised = 0;
            EventHandler h = (_, _) => raised++;
            ChatCommandRegistry.RegistryChanged += h;
            try
            {
                int v = ChatCommandRegistry.Version;
                ChatCommandRegistry.Add("jump");
                ChatCommandRegistry.Remove("jump");
                ChatCommandRegistry.Remove("not-there");
                Assert.Equal(2, raised);
                Assert.Equal(v + 2, ChatCommandRegistry.Version);
            }
            finally { ChatCommandRegistry.RegistryChanged -= h; }
        }

        // ── Yuriy's grammar (ForgePlay CommandParser.js) ──

        [Fact]
        public void APlainWordPressesForTwoHundredMilliseconds()
        {
            var events = T("a", out int end);
            Assert.Equal(new[] { new ChatEvent(0, B("a"), true), new ChatEvent(200, B("a"), false) }, events);
            Assert.Equal(200, end);
        }

        [Theory]
        [InlineData("a500ms", 500)]
        [InlineData("a1s", 1000)]
        [InlineData("a1.5s", 1500)]
        [InlineData("A500ms", 500)]
        public void ATimeAtTheEndSetsTheHold(string text, int ms)
        {
            var action = Assert.Single(Assert.Single(P(text)).Actions);
            Assert.Equal(new ChatAction(B("a"), ChatActionKind.Press, ms), action);
        }

        [Fact]
        public void APlusPressesBoth_AndTheStepLastsAsLongAsTheLongest()
        {
            var events = T("a+b500ms x", out int end);
            Assert.Equal(new[]
            {
                new ChatEvent(0, B("a"), true),
                new ChatEvent(0, B("b"), true),
                new ChatEvent(200, B("a"), false),
                new ChatEvent(500, B("b"), false),
                new ChatEvent(500, B("x"), true),
                new ChatEvent(700, B("x"), false),
            }, events);
            Assert.Equal(700, end);
        }

        [Fact]
        public void AHoldStaysDownUntilReleased_AndATimedReleasePressesAgain()
        {
            // ForgePlay's executor: a release with a time presses the button
            // again when the time runs out.
            var events = T("_a #300ms -a100ms #50ms -a", out int end);
            Assert.Equal(new[]
            {
                new ChatEvent(0, B("a"), true),
                new ChatEvent(300, B("a"), false),
                new ChatEvent(400, B("a"), true),
                new ChatEvent(450, B("a"), false),
            }, events);
            Assert.Equal(450, end);
        }

        [Fact]
        public void AGroupRepeats_AndAGroupTimeAppliesToEachPress()
        {
            Assert.Equal(6, P("[a b]*3").Count);
            var steps = P("[a+b]500ms");
            var step = Assert.Single(steps);
            Assert.All(step.Actions, a => Assert.Equal(500, a.DurationMs));
            // A word with its own time inside a timed group keeps its own.
            Assert.Equal(100, Assert.Single(P("[a100ms]500ms")).Actions[0].DurationMs);
        }

        [Theory]
        [InlineData("jump")]          // a word the list does not have
        [InlineData("a+")]            // nothing after the plus
        [InlineData("[a b")]          // a group never closed
        [InlineData("a]")]            // a close with no open
        [InlineData("[a]*0")]         // no repeats
        [InlineData("[a]xyz")]        // a group suffix that is no time
        [InlineData("#5")]            // a pause with no unit
        [InlineData("a11s")]          // past the ten-second limit
        [InlineData("[a]*51")]        // more steps than the limit
        [InlineData("[[[[[a]]]]]")]   // nested past four
        [InlineData("_")]             // a hold of nothing
        public void ACommandThatBreaksARuleOrALimitDoesNotPlayAtAll(string text)
            => Assert.Null(P(text));

        [Fact]
        public void TheLimitsHold_TenSecondsAndFiftySteps()
        {
            Assert.NotNull(P("a10s"));
            Assert.Null(P("a5s a5s a1ms"));
            Assert.NotNull(P("[a1ms]*50"));
            Assert.Null(P("[a1ms]*50 b"));
            Assert.Null(P(new string('a', 501)));
        }

        // ── The release gap between presses of one button ──

        [Fact]
        public void APressOfTheSameButtonWaitsUntilItHasBeenUpForFiftyMilliseconds()
        {
            int a = B("a");
            var events = T("a a", out int end);
            Assert.Equal(new[]
            {
                new ChatEvent(0, a, true), new ChatEvent(200, a, false),
                new ChatEvent(250, a, true), new ChatEvent(450, a, false),
            }, events);
            Assert.Equal(450, end);
        }

        [Fact]
        public void EachRepeatOfAGroupIsItsOwnPress()
            => Assert.Equal(new[] { 0, 250, 500 }, T("[a]*3", out _).Where(e => e.Down).Select(e => e.AtMs));

        [Fact]
        public void APressOfAnotherButtonNeedsNoGap()
            => Assert.Equal(new[] { 0, 200 }, T("a b", out _).Where(e => e.Down).Select(e => e.AtMs));

        [Fact]
        public void AChordWaitsWhole_ForTheButtonThatJustWentUp()
        {
            var events = T("a a+b", out _);
            Assert.Contains(new ChatEvent(250, B("a"), true), events);
            Assert.Contains(new ChatEvent(250, B("b"), true), events);
        }

        [Fact]
        public void APauseShorterThanTheGapIsStretchedToIt()
            => Assert.Equal(new[] { 0, 250 }, T("a #20ms a", out _).Where(e => e.Down).Select(e => e.AtMs));

        [Fact]
        public void APressAfterARelease_AndABriefTimedRelease_KeepTheGap()
        {
            int a = B("a");
            Assert.Contains(new ChatEvent(150, a, true), T("_a #100ms -a a", out _));
            var events = T("_a -a20ms", out int end);
            Assert.Equal(new[] { new ChatEvent(0, a, true), new ChatEvent(0, a, false), new ChatEvent(50, a, true) }, events);
            Assert.Equal(50, end);
        }

        [Fact]
        public void AButtonReleasedAndPressedInOneStepStillGetsTheGap()
            => Assert.Equal(new[] { 0, 50 }, T("_a -a+a", out _).Where(e => e.Down).Select(e => e.AtMs));

        [Fact]
        public void TheGapCountsTowardTheTenSecondLimit()
        {
            Assert.NotNull(P("a5s b5s"));
            Assert.Null(P("a5s a5s"));
        }

        // ── A group with nothing in it ──

        [Theory]
        [InlineData("[] a")]
        [InlineData("a [ ]")]
        [InlineData("[[] a]")]
        [InlineData("[]*50 a")]
        [InlineData("[]500ms a")]
        public void AnEmptyGroupBreaksTheRules_SoTheCommandDoesNotPlay(string text)
            => Assert.Null(P(text));

        [Fact]
        public void AMessageOfEmptyGroupsEndsAtTheFirstOne()
        {
            string bomb = "[[[" + string.Concat(Enumerable.Repeat("[]*50 ", 80)) + "]*50]*50]*50";
            Assert.True(bomb.Length <= ChatCommandParser.MaxLength);
            Assert.Null(ChatCommandParser.Parse(bomb, ChatCommandRegistry.ButtonFor, out int groups));
            // The read that goes over the budget is counted before it is refused.
            Assert.True(groups <= ChatCommandParser.MaxGroupParses + 1, $"{groups} groups");
        }

        [Fact]
        public void TheLongestCommandTheLimitsAllowStaysInsideTheParseBudget()
        {
            string text = string.Join(" ", Enumerable.Range(0, ChatCommandParser.MaxSteps)
                .Select(i => i % 2 == 0 ? "[[[[a]]]]" : "[[[[b]]]]"));
            Assert.True(text.Length <= ChatCommandParser.MaxLength);
            var steps = ChatCommandParser.Parse(text, ChatCommandRegistry.ButtonFor, out int groups);
            Assert.Equal(ChatCommandParser.MaxSteps, steps?.Count);
            Assert.Equal(ChatCommandParser.MaxGroupParses, groups);
        }

        // ── Taking turns (ForgePlay ClaimControl.js, SkipControl.js) ──

        private sealed class Clock { public long Now; }

        [Fact]
        public void WithViewersChosen_AClaimNeedsTwoOtherViewers_ThenGivesFiveMinutesOfSoleControl()
        {
            var clock = new Clock { Now = 1000 };
            var turns = new ChatTurns(() => clock.Now, () => TwitchClaimApproval.Viewers);
            Assert.True(turns.Handle("Alice", false, "claim", "", false, null));
            Assert.Null(turns.Owner);
            Assert.True(turns.CanPlay("Bob", false));
            // Her own vote does not count.
            turns.Handle("alice", false, "accept", "@alice", false, null);
            turns.Handle("Bob", false, "accept", "@Alice", false, null);
            Assert.Null(turns.Owner);
            turns.Handle("Carol", false, "claim", "@alice", false, null);
            Assert.Equal("Alice", turns.Owner);
            Assert.True(turns.CanPlay("alice", false));
            Assert.False(turns.CanPlay("Bob", false));
            Assert.True(turns.CanPlay("Mod", true));
            clock.Now += ChatTurns.ControlMs;
            Assert.Null(turns.Owner);
            Assert.True(turns.CanPlay("Bob", false));
        }

        [Fact]
        public void ARequestExpiresAfterSixtySeconds_AndOnlyFiveWait()
        {
            var clock = new Clock { Now = 0 };
            var turns = new ChatTurns(() => clock.Now);
            for (int i = 0; i < 6; i++) turns.Handle("v" + i, false, "claim", "", false, null);
            Assert.Equal(5, turns.PendingRequests.Count);
            clock.Now += ChatTurns.VoteMs;
            Assert.Empty(turns.PendingRequests);
        }

        [Fact]
        public void WithModeratorsChosen_AModeratorGrantsAtOnce_AndEndsAnotherViewersControl()
        {
            var turns = new ChatTurns(() => 0, () => TwitchClaimApproval.Moderators);
            turns.Handle("Mod", true, "claim", "@Dave", false, null);
            Assert.Equal("Dave", turns.Owner);
            turns.Handle("Eve", false, "release", "@Dave", false, null);
            Assert.Equal("Dave", turns.Owner);
            turns.Handle("Mod", true, "release", "@dave", false, null);
            Assert.Null(turns.Owner);
        }

        [Fact]
        public void TheOwnerReleases_AndTheStreamerEndsAClaim()
        {
            var turns = new ChatTurns(() => 0, () => TwitchClaimApproval.Streamer);
            turns.Handle("Host", true, "claim", "@Dave", false, null, broadcaster: true);
            turns.Handle("Dave", false, "release", "", false, null);
            Assert.Null(turns.Owner);
            turns.Handle("Host", true, "claim", "@Dave", false, null, broadcaster: true);
            turns.EndClaim();
            Assert.Null(turns.Owner);
            Assert.Contains("streamer", turns.Notice);
        }

        // ── Who approves a claim ──

        [Fact]
        public void ByDefault_OnlyTheStreamerApproves_AndNoVoteCounts()
        {
            Assert.Equal(TwitchClaimApproval.Streamer, TwitchChatRuntime.ClaimApproval);
            var turns = new ChatTurns(() => 0);
            Assert.Equal(0, turns.VotesNeeded);
            turns.Handle("Alice", false, "claim", "", false, null);
            Assert.Contains("The streamer can approve with !accept @Alice", turns.Notice);
            // Three accounts of one troll, and a moderator by both commands.
            turns.Handle("Bob", false, "accept", "@Alice", false, null);
            Assert.Contains("only the streamer approves claims", turns.Notice);
            turns.Handle("Carol", false, "claim", "@alice", false, null);
            turns.Handle("Dan", false, "accept", "@alice", false, null);
            turns.Handle("Mod", true, "accept", "@Alice", false, null);
            turns.Handle("Mod", true, "claim", "@Alice", false, null);
            Assert.Null(turns.Owner);
            Assert.Equal(new[] { 0 }, turns.PendingRequests.Select(r => r.Votes));
            turns.Handle("Host", true, "accept", "@alice", false, null, broadcaster: true);
            Assert.Equal("Alice", turns.Owner);
            Assert.Contains("@Host gave @Alice control", turns.Notice);
        }

        [Fact]
        public void WithModeratorsChosen_AModeratorsAcceptGrants_AndAViewersIsRefused()
        {
            var turns = new ChatTurns(() => 0, () => TwitchClaimApproval.Moderators);
            Assert.Equal(0, turns.VotesNeeded);
            turns.Handle("Alice", false, "claim", "", false, null);
            Assert.Contains("The streamer or a moderator can approve", turns.Notice);
            turns.Handle("Bob", false, "accept", "@Alice", false, null);
            turns.Handle("Carol", false, "accept", "@Alice", false, null);
            Assert.Contains("only the streamer and moderators approve claims", turns.Notice);
            Assert.Null(turns.Owner);
            turns.Handle("Mod", true, "accept", "@Alice", false, null);
            Assert.Equal("Alice", turns.Owner);
        }

        [Fact]
        public void WithViewersChosen_AModeratorsAcceptGrantsAtOnceToo()
        {
            var turns = new ChatTurns(() => 0, () => TwitchClaimApproval.Viewers);
            Assert.Equal(ChatTurns.RequiredVotes, turns.VotesNeeded);
            turns.Handle("Alice", false, "claim", "", false, null);
            Assert.Contains($"{ChatTurns.RequiredVotes} other viewers can approve", turns.Notice);
            turns.Handle("Mod", true, "accept", "@Alice", false, null);
            Assert.Equal("Alice", turns.Owner);
        }

        [Fact]
        public void AnApproversAccept_NeedsAWaitingRequest()
        {
            var turns = new ChatTurns(() => 0, () => TwitchClaimApproval.Streamer);
            turns.Handle("Host", true, "accept", "@Nobody", false, null, broadcaster: true);
            Assert.Null(turns.Owner);
            Assert.Contains("@nobody has not asked for control", turns.Notice);
        }

        [Fact]
        public void TheChoiceIsReadOnEveryCommand()
        {
            var choice = TwitchClaimApproval.Streamer;
            var turns = new ChatTurns(() => 0, () => choice);
            turns.Handle("Alice", false, "claim", "", false, null);
            turns.Handle("Bob", false, "accept", "@Alice", false, null);
            choice = TwitchClaimApproval.Viewers;
            // Bob's refused try did not count. Two votes cast now do.
            turns.Handle("Carol", false, "accept", "@Alice", false, null);
            Assert.Null(turns.Owner);
            turns.Handle("Bob", false, "accept", "@Alice", false, null);
            Assert.Equal("Alice", turns.Owner);
        }

        [Fact]
        public void TheStatusShowsVotesOnlyWhileViewersApprove()
        {
            var choice = TwitchClaimApproval.Streamer;
            var turns = new ChatTurns(() => 0, () => choice);
            turns.Handle("Alice", false, "claim", "", false, null);
            turns.Handle("Bob", false, "claim", "status", false, null);
            Assert.Equal("Waiting: @Alice 60 seconds.", turns.Notice);
            choice = TwitchClaimApproval.Viewers;
            turns.Handle("Bob", false, "claim", "status", false, null);
            Assert.Equal("Waiting: @Alice 0 of 2, 60 seconds.", turns.Notice);
        }

        [Fact]
        public void ASkipNeedsTwoViewersWithinThirtySeconds_OrOneModerator()
        {
            var clock = new Clock { Now = 0 };
            var turns = new ChatTurns(() => clock.Now);
            var skips = new List<bool>();
            bool Skip(bool all) { skips.Add(all); return true; }

            turns.Handle("A", false, "skip", "", running: false, Skip);
            Assert.Empty(skips);
            turns.Handle("A", false, "skip", "", running: true, Skip);
            turns.Handle("A", false, "skip", "", running: true, Skip);
            Assert.Empty(skips);
            clock.Now += ChatTurns.SkipWindowMs + 1;
            // The window closed: B starts a new vote rather than confirming.
            turns.Handle("B", false, "skip", "", running: true, Skip);
            Assert.Empty(skips);
            turns.Handle("C", false, "skip", "", running: true, Skip);
            Assert.Equal(new[] { false }, skips);
            turns.Handle("Mod", true, "skipall", "", running: true, Skip);
            Assert.Equal(new[] { false, true }, skips);
        }

        [Fact]
        public void ASkipThatStopsNothingSaysSo()
        {
            var turns = new ChatTurns(() => 0);
            turns.Handle("Mod", true, "skip", "", running: true, _ => false);
            Assert.Contains("nothing was available to skip", turns.Notice);
        }

        // ── A notice goes on the broadcast ──

        [Fact]
        public void ANoticeNeverRepeatsWhatAViewerTyped()
        {
            var turns = new ChatTurns(() => 0, () => TwitchClaimApproval.Viewers);
            turns.Handle("Troll", false, "claim", "buy-my-stuff", false, null);
            Assert.DoesNotContain("buy-my-stuff", turns.Notice);
            Assert.Contains("@Troll", turns.Notice);
            turns.Handle("Troll", false, "claim", "@buy-my-stuff", false, null);
            Assert.DoesNotContain("buy-my-stuff", turns.Notice);
            turns.Handle("Troll", false, "accept", "@buy-my-stuff", false, null);
            Assert.DoesNotContain("buy-my-stuff", turns.Notice);
        }

        // ── A viewer is their login ──

        [Fact]
        public void AVoteAndARequestAreCountedByLogin_WhateverNameIsShown()
        {
            var turns = new ChatTurns(() => 0, () => TwitchClaimApproval.Viewers);
            const string Shown = "\u5C0F\u660E";
            turns.Handle(Shown, false, "claim", "", false, null, login: "xiaoming");
            // Asking again under the same login is the same request.
            turns.Handle(Shown, false, "claim", "", false, null, login: "xiaoming");
            Assert.Single(turns.PendingRequests);
            // Their own vote does not count, by either name.
            turns.Handle(Shown, false, "accept", "@xiaoming", false, null, login: "xiaoming");
            turns.Handle(Shown, false, "accept", "@" + Shown, false, null, login: "xiaoming");
            Assert.Equal(0, turns.PendingRequests[0].Votes);
            // Two accounts showing one name are two voters.
            turns.Handle("Same", false, "accept", "@" + Shown, false, null, login: "one");
            turns.Handle("Same", false, "accept", "@xiaoming", false, null, login: "two");
            Assert.Equal(Shown, turns.Owner);
            Assert.True(turns.CanPlay(Shown, false, "xiaoming"));
            Assert.False(turns.CanPlay(Shown, false, "impostor"));
            turns.Handle(Shown, false, "release", "", false, null, login: "impostor");
            Assert.Equal(Shown, turns.Owner);
            turns.Handle(Shown, false, "release", "", false, null, login: "xiaoming");
            Assert.Null(turns.Owner);
        }

        [Fact]
        public void TwoAccountsShowingOneNameAreTwoSkipVotes()
        {
            var turns = new ChatTurns(() => 0);
            int skips = 0;
            bool Skip(bool all) { skips++; return true; }
            turns.Handle("Same", false, "skip", "", true, Skip, login: "one");
            turns.Handle("Same", false, "skip", "", true, Skip, login: "one");
            Assert.Equal(0, skips);
            turns.Handle("Same", false, "skip", "", true, Skip, login: "two");
            Assert.Equal(1, skips);
        }

        [Fact]
        public void AModeratorEndsAClaimByLoginOrByTheNameShown()
        {
            var turns = new ChatTurns(() => 0, () => TwitchClaimApproval.Moderators);
            const string Shown = "\u5C0F\u660E";
            turns.Handle(Shown, false, "claim", "", false, null, login: "xiaoming");
            turns.Handle("Mod", true, "accept", "@xiaoming", false, null);
            turns.Handle("Mod", true, "release", "@xiaoming", false, null);
            Assert.Null(turns.Owner);
            turns.Handle(Shown, false, "claim", "", false, null, login: "xiaoming");
            turns.Handle("Mod", true, "accept", "@" + Shown, false, null);
            Assert.Equal(Shown, turns.Owner);
            turns.Handle("Mod", true, "release", "@" + Shown, false, null);
            Assert.Null(turns.Owner);
        }

        [Fact]
        public void AnythingElseIsNotATurnCommand()
            => Assert.False(new ChatTurns(() => 0).Handle("A", false, "a500ms", "", false, null));
    }
}
