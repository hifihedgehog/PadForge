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
        public TwitchChatCommandTests() => ChatCommandRegistry.LoadRegistry(null);
        public void Dispose() => ChatCommandRegistry.LoadRegistry(null);

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

        // ── Taking turns (ForgePlay ClaimControl.js, SkipControl.js) ──

        private sealed class Clock { public long Now; }

        [Fact]
        public void AClaimNeedsTwoOtherViewers_ThenGivesFiveMinutesOfSoleControl()
        {
            var clock = new Clock { Now = 1000 };
            var turns = new ChatTurns(() => clock.Now);
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
        public void AModeratorGrantsAtOnce_AndEndsAnotherViewersControl()
        {
            var turns = new ChatTurns(() => 0);
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
            var turns = new ChatTurns(() => 0);
            turns.Handle("Mod", true, "claim", "@Dave", false, null);
            turns.Handle("Dave", false, "release", "", false, null);
            Assert.Null(turns.Owner);
            turns.Handle("Mod", true, "claim", "@Dave", false, null);
            turns.EndClaim();
            Assert.Null(turns.Owner);
            Assert.Contains("streamer", turns.Notice);
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

        [Fact]
        public void AnythingElseIsNotATurnCommand()
            => Assert.False(new ChatTurns(() => 0).Handle("A", false, "a500ms", "", false, null));
    }
}
