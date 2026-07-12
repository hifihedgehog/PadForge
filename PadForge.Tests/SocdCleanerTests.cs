using PadForge.Common.Input;

namespace PadForge.Tests
{
    /// <summary>Covers the #205 SOCD cleaner (Snap Tap): the last-wins /
    /// neutral / first-wins per-pair semantics from hitboxer's OPPOSITE and
    /// NEUTRAL modes, applied as a frame bitset transform, plus pair
    /// independence, non-pair passthrough, and Off passthrough.</summary>
    public class SocdCleanerTests
    {
        private const int W = 0x57;
        private const int S = 0x53;
        private const int A = 0x41;
        private const int D = 0x44;
        private const int Space = 0x20;

        private static ulong[] Frame(SocdCleaner socd, params int[] vks)
        {
            var w = new ulong[4];
            foreach (int vk in vks)
                w[vk >> 6] |= 1UL << (vk & 63);
            socd.Apply(ref w[0], ref w[1], ref w[2], ref w[3]);
            return w;
        }

        private static bool Down(ulong[] w, int vk)
            => (w[vk >> 6] & (1UL << (vk & 63))) != 0;

        [Fact]
        public void LastWins_Press_B_While_A_Held_Suppresses_A()
        {
            var socd = new SocdCleaner();
            socd.Configure("LastWins", "87:83");

            var f1 = Frame(socd, W);
            Assert.True(Down(f1, W));

            var f2 = Frame(socd, W, S);
            Assert.False(Down(f2, W));
            Assert.True(Down(f2, S));
        }

        [Fact]
        public void LastWins_Release_B_RePresses_Still_Held_A()
        {
            var socd = new SocdCleaner();
            socd.Configure("LastWins", "87:83");

            Frame(socd, W);
            var both = Frame(socd, W, S);
            Assert.False(Down(both, W));

            // S released, W still physically held: its bit passes through
            // again, so the VC's change detection emits a fresh key-down.
            var f3 = Frame(socd, W);
            Assert.True(Down(f3, W));
            Assert.False(Down(f3, S));
        }

        [Fact]
        public void LastWins_Alternating_Taps_Flip_The_Winner()
        {
            var socd = new SocdCleaner();
            socd.Configure("LastWins", "87:83");

            Frame(socd, W);
            var f2 = Frame(socd, W, S);      // S newly down: S wins
            Assert.False(Down(f2, W));
            Assert.True(Down(f2, S));

            var f3 = Frame(socd, S);         // W released
            Assert.True(Down(f3, S));

            var f4 = Frame(socd, W, S);      // W newly down: W wins
            Assert.True(Down(f4, W));
            Assert.False(Down(f4, S));
        }

        [Fact]
        public void Neutral_Both_Held_Clears_Both_Then_Survivor_Returns()
        {
            var socd = new SocdCleaner();
            socd.Configure("Neutral", "87:83");

            Frame(socd, W);
            var both = Frame(socd, W, S);
            Assert.False(Down(both, W));
            Assert.False(Down(both, S));

            var f3 = Frame(socd, S);
            Assert.True(Down(f3, S));
        }

        [Fact]
        public void FirstWins_Later_Press_Loses_Until_Winner_Releases()
        {
            var socd = new SocdCleaner();
            socd.Configure("FirstWins", "87:83");

            Frame(socd, W);
            var both = Frame(socd, W, S);
            Assert.True(Down(both, W));
            Assert.False(Down(both, S));

            // Still both held: the earlier press keeps the win.
            var held = Frame(socd, W, S);
            Assert.True(Down(held, W));
            Assert.False(Down(held, S));

            // Winner released: the held loser comes through.
            var f4 = Frame(socd, S);
            Assert.True(Down(f4, S));
        }

        [Fact]
        public void SameFrame_Press_Is_Deterministic_Per_Mode()
        {
            var last = new SocdCleaner();
            last.Configure("LastWins", "87:83");
            var lf = Frame(last, W, S);
            Assert.False(Down(lf, W));
            Assert.True(Down(lf, S));

            var first = new SocdCleaner();
            first.Configure("FirstWins", "87:83");
            var ff = Frame(first, W, S);
            Assert.True(Down(ff, W));
            Assert.False(Down(ff, S));
        }

        [Fact]
        public void Multiple_Pairs_Resolve_Independently()
        {
            var socd = new SocdCleaner();
            socd.Configure("LastWins", "87:83|65:68");

            Frame(socd, W, A);
            var f2 = Frame(socd, W, S, A);   // W/S conflicted, A/D not
            Assert.False(Down(f2, W));
            Assert.True(Down(f2, S));
            Assert.True(Down(f2, A));

            var f3 = Frame(socd, W, S, A, D);
            Assert.False(Down(f3, W));
            Assert.True(Down(f3, S));
            Assert.False(Down(f3, A));
            Assert.True(Down(f3, D));
        }

        [Fact]
        public void NonPair_Keys_Pass_Through_Untouched()
        {
            var socd = new SocdCleaner();
            socd.Configure("LastWins", "87:83");

            Frame(socd, Space, W);
            var f2 = Frame(socd, Space, W, S);
            Assert.True(Down(f2, Space));
            Assert.False(Down(f2, W));
            Assert.True(Down(f2, S));
        }

        [Fact]
        public void Off_Mode_Is_A_Passthrough()
        {
            var socd = new SocdCleaner();
            socd.Configure("Off", PadForge.ViewModels.KbmSlotConfig.DefaultSocdPairs);

            var f = Frame(socd, W, S);
            Assert.True(Down(f, W));
            Assert.True(Down(f, S));
        }

        [Fact]
        public void Malformed_And_Self_Pairs_Are_Dropped()
        {
            var socd = new SocdCleaner();
            socd.Configure("LastWins", "87:87|junk|:5|7:|87:83");

            Frame(socd, W);
            var f2 = Frame(socd, W, S);      // the one valid pair still works
            Assert.False(Down(f2, W));
            Assert.True(Down(f2, S));
        }

        [Fact]
        public void Enabled_Mid_Hold_Is_Deterministic()
        {
            // Neither key is a fresh press on the first conflicted frame
            // (mode turned on while both were already held): LastWins picks
            // B, FirstWins picks A.
            byte winner = SocdCleaner.WinNone;
            SocdCleaner.StepPair(SocdCleaner.SocdMode.LastWins,
                a: true, b: true, prevA: true, prevB: true, ref winner,
                out bool supA, out bool supB);
            Assert.True(supA);
            Assert.False(supB);

            winner = SocdCleaner.WinNone;
            SocdCleaner.StepPair(SocdCleaner.SocdMode.FirstWins,
                a: true, b: true, prevA: true, prevB: true, ref winner,
                out supA, out supB);
            Assert.False(supA);
            Assert.True(supB);
        }
    }
}
