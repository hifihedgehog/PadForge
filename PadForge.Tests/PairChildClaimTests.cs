using System.Collections.Generic;
using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Round-34 guard for the combined Joy-Con pair's child resolution.
    ///
    /// <para>Two combined pairs (four Joy-Cons, SDL's HIDAPI_COMBINE_JOY_CONS
    /// default) expose two lefts and two rights. The old resolver called
    /// FindHidPath(0x2006)/FindHidPath(0x2007), which return the FIRST present
    /// match, so both sinks opened the same physical pair: one pair driven by
    /// two writers with two independent rolling 4-bit timers, the other silent
    /// behind a sink that reported itself live. FirstUnclaimed is the picker
    /// that ends that, and these tests pin its behavior.</para>
    /// </summary>
    public class PairChildClaimTests
    {
        private static readonly string LeftA = @"\\?\hid#vid_057e&pid_2006#a";
        private static readonly string LeftB = @"\\?\hid#vid_057e&pid_2006#b";
        private static readonly string RightA = @"\\?\hid#vid_057e&pid_2007#a";

        [Fact]
        public void SingleSink_TakesTheFirstChild()
        {
            var claimed = new HashSet<string>();
            Assert.Equal(LeftA, HapticToneService.FirstUnclaimed(
                new List<string> { LeftA, LeftB }, claimed));
        }

        [Fact]
        public void SecondSink_SkipsTheClaimedChild()
        {
            // The exact four-Joy-Con case: sink A holds LeftA, so sink B must
            // land on LeftB. Pre-fix both received LeftA.
            var claimed = new HashSet<string> { LeftA };
            Assert.Equal(LeftB, HapticToneService.FirstUnclaimed(
                new List<string> { LeftA, LeftB }, claimed));
        }

        [Fact]
        public void ClaimMatching_IsCaseInsensitive()
        {
            // SetupDi and CreateFileW both accept either casing for a device
            // path, so a case-sensitive claim set would leak a double-open.
            var claimed = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
                { LeftA.ToUpperInvariant() };
            Assert.Equal(LeftB, HapticToneService.FirstUnclaimed(
                new List<string> { LeftA, LeftB }, claimed));
        }

        [Fact]
        public void AllClaimed_YieldsNull_NotAStolenHandle()
        {
            // One pair, two sinks: the second gets nothing rather than a
            // second writer on the first sink's coils. SelectPairChildPaths
            // then keeps the synthetic path and Reconcile retries every 3 s.
            var claimed = new HashSet<string> { LeftA, RightA };
            Assert.Null(HapticToneService.FirstUnclaimed(
                new List<string> { LeftA }, claimed));
            Assert.Null(HapticToneService.FirstUnclaimed(
                new List<string> { RightA }, claimed));
        }

        [Fact]
        public void EmptyAndNullCandidates_AreSafe()
        {
            Assert.Null(HapticToneService.FirstUnclaimed(null, new HashSet<string>()));
            Assert.Null(HapticToneService.FirstUnclaimed(new List<string>(), null));
        }

        [Fact]
        public void NullEntriesAreSkipped_NotReturned()
        {
            // FindHidPaths never adds a null, but a defensive skip keeps a
            // null from being handed to CreateFileW if that ever changes.
            Assert.Equal(LeftA, HapticToneService.FirstUnclaimed(
                new List<string> { null, "", LeftA }, null));
        }

        [Fact]
        public void UnclaimedPick_FeedsSelectPairChildPaths_Unchanged()
        {
            // The picker sits in front of the existing selector: left present
            // still means left-primary, and a missing left still promotes the
            // right with PrimaryIsRight set.
            var (p1, s1, isRight1) = HapticToneService.SelectPairChildPaths(LeftB, RightA);
            Assert.Equal(LeftB, p1);
            Assert.Equal(RightA, s1);
            Assert.False(isRight1);

            var (p2, s2, isRight2) = HapticToneService.SelectPairChildPaths(null, RightA);
            Assert.Equal(RightA, p2);
            Assert.Null(s2);
            Assert.True(isRight2);
        }
    }
}
