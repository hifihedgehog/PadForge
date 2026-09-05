using System.IO;
using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>The Flydigi Enhanced Protocol switch is one static. Every
    /// test class that flips it rides this collection, serialized, or a
    /// class writing Off can interleave with another writing On.</summary>
    [CollectionDefinition("FlydigiSwitchStatics", DisableParallelization = true)]
    public class FlydigiSwitchStaticsCollection { }

    /// <summary>
    /// Discussion #395, second evidence set: SDL's Flydigi probe failed on two
    /// of three arrivals ("driver = NONE") while Flydigi's own service, one
    /// second later, was answered in 24 ms, and the slot bound to the enhanced
    /// view stayed offline. PadForge asks SDL to probe again after the pad's
    /// first second by changing the hint's string and not its meaning. The
    /// identity is the vendor interface's HID path.
    /// </summary>
    [Collection("FlydigiSwitchStatics")]
    public class FlydigiReprobeTests
    {
        private const int D = FlydigiReprobePolicy.DelayMs;
        private const string A = @"\\?\HID#VID_37D7&PID_2401&MI_01#7&2e838171&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        private const string B = @"\\?\HID#VID_37D7&PID_2401&MI_01#7&0abc0abc&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        private static readonly string[] None = new string[0];

        [Fact]
        public void AnUnclaimedInterface_IsProbedAfterTheDelay_ThenSpaced()
        {
            var p = new FlydigiReprobePolicy();
            Assert.Empty(p.Observe(1000, new[] { A }, None, false));           // first seen: deadline set, no probe
            Assert.True(p.Armed);
            Assert.Empty(p.Observe(1000 + D - 1, new[] { A }, None, false));   // not yet
            var due = p.Observe(1000 + D, new[] { A }, None, false);
            Assert.Equal(new[] { A }, due);
            Assert.Equal(1, p.LastAttempt);
            Assert.Empty(p.Observe(1000 + D + 10, new[] { A }, None, false)); // spaced
            Assert.Single(p.Observe(1000 + 2 * D, new[] { A }, None, false));
            Assert.Equal(2, p.LastAttempt);
        }

        [Fact]
        public void AClaimedInterface_IsNeverProbed_AndStaysDoneIfItsViewLaterGoes()
        {
            var p = new FlydigiReprobePolicy();
            Assert.Empty(p.Observe(0, new[] { A }, new[] { A.ToUpperInvariant() }, false));   // case-insensitive
            Assert.False(p.Armed);
            Assert.Empty(p.Observe(10 * D, new[] { A }, None, false));        // the enhanced view went away: startup recovery only
            Assert.False(p.Armed);
        }

        [Fact]
        public void TwoIdenticalPads_AreTwoPaths_AndOnlyTheUnclaimedOneIsProbed()
        {
            var p = new FlydigiReprobePolicy();
            p.Observe(0, new[] { A, B }, new[] { A }, false);
            var due = p.Observe(D, new[] { A, B }, new[] { A }, false);
            Assert.Equal(new[] { B }, due);
            // B recovers: its path shows up claimed, and nothing is left to do.
            Assert.Empty(p.Observe(2 * D, new[] { A, B }, new[] { A, B }, false));
            Assert.False(p.Armed);
        }

        [Fact]
        public void APadWithNoXInputView_IsStillSeen()
        {
            // The interface is present on the bus whether or not PadForge has
            // any joystick view of the pad. Presence is what is observed.
            var p = new FlydigiReprobePolicy();
            p.Observe(0, new[] { A }, None, false);
            Assert.Single(p.Observe(D, new[] { A }, None, false));
        }

        [Fact]
        public void FourAttempts_ThenTheProbesStop_UntilThePathLeavesAndReturns()
        {
            var p = new FlydigiReprobePolicy();
            int fired = 0;
            for (long t = 0; t <= D * 12; t += 100)
                if (p.Observe(t, new[] { A }, None, false).Count > 0) fired++;
            Assert.Equal(FlydigiReprobePolicy.MaxAttempts, fired);
            Assert.False(p.Armed);
            Assert.Equal(1, p.Tracked);
            // Gone from the bus and back: a fresh deadline and budget.
            p.Observe(100_000, None, None, false);
            Assert.Equal(0, p.Tracked);
            p.Observe(100_100, new[] { A }, None, false);
            Assert.True(p.Armed);
            Assert.Empty(p.Observe(100_100 + D - 1, new[] { A }, None, false));
            Assert.Single(p.Observe(100_100 + D, new[] { A }, None, false));
            Assert.Equal(1, p.LastAttempt);
        }

        [Fact]
        public void ASecondPadArriving_DoesNotRenewTheFirstPadsSpentBudget()
        {
            var p = new FlydigiReprobePolicy();
            for (long t = 0; t <= D * 12; t += 100) p.Observe(t, new[] { A }, None, false);
            Assert.False(p.Armed);
            p.Observe(100_000, new[] { A, B }, None, false);       // B arrives, A is still present and spent
            Assert.True(p.Armed);
            var due = p.Observe(100_000 + D, new[] { A, B }, None, false);
            Assert.Equal(new[] { B }, due);                         // A stays silent
            int fired = 1;
            for (long t = 100_000 + D + 100; t <= 100_000 + D * 12; t += 100)
                if (p.Observe(t, new[] { A, B }, None, false).Count > 0) fired++;
            Assert.Equal(FlydigiReprobePolicy.MaxAttempts, fired);
        }

        [Fact]
        public void FluxDefersEveryDeadline_SoAQuickReplugGetsItsFullDelay()
        {
            // The old wrapper is detached and awaiting cleanup from 500 to
            // 2000, and the pad came back at 1000 with a failed probe. The
            // probe must not fire at 1200, 200 ms into the new connection.
            var p = new FlydigiReprobePolicy();
            p.Observe(0, new[] { A }, None, false);
            Assert.Empty(p.Observe(1200, new[] { A }, None, inFlux: true));    // deferred to 2400
            Assert.Empty(p.Observe(1900, new[] { A }, None, inFlux: true));    // deferred to 3100
            Assert.Empty(p.Observe(2400, new[] { A }, None, false));           // still not due
            Assert.Single(p.Observe(3100, new[] { A }, None, false));
        }

        [Theory]
        [InlineData("1", "true")]
        [InlineData("true", "1")]
        [InlineData("0", "1")]
        [InlineData(null, "1")]
        public void TheHintString_Alternates_AndAlwaysMeansOn(string current, string expected)
            => Assert.Equal(expected, FlydigiReprobePolicy.NextHintValue(current));

        [Fact]
        public void SdlHidEnumeration_Marshals_AgainstThisMachinesDevices()
        {
            // Any vendor, any usage page: the machine's own keyboard and mouse
            // collections are enough to walk the native list end to end. The
            // Flydigi filter on this bench is empty and must not throw.
            var all = PadForge.Engine.SdlHidEnumeration.Paths(0, 0);
            Assert.All(all, path => Assert.False(string.IsNullOrWhiteSpace(path)));
            var flydigi = PadForge.Engine.SdlHidEnumeration.Paths(0x37D7, 0xFFA0);
            Assert.NotNull(flydigi);
        }

        [Fact]
        public void TheSwitchAndTheNudge_ShareOneLock_TakeTheJoystickLock_AndTheTickObservesPaths()
        {
            string im = File.ReadAllText(Path.Combine(RepoRoot(), "PadForge.App", "Common", "Input", "InputManager.cs"));
            int apply = im.IndexOf("public static void ApplyFlydigiEnhancedProtocol(bool enabled)", System.StringComparison.Ordinal);
            Assert.True(apply > 0);
            string applyBody = im.Substring(apply, 1100);
            Assert.Contains("lock (_flydigiHintLock)", applyBody);
            Assert.Contains("WriteFlydigiHintUnderJoystickLock(", applyBody);
            int nudge = im.IndexOf("internal static (bool written, string value) TryFlydigiReprobeNudge()", System.StringComparison.Ordinal);
            Assert.True(nudge > 0);
            string body = im.Substring(nudge, 900);
            Assert.Contains("lock (_flydigiHintLock)", body);
            int gate = body.IndexOf("if (!_flydigiEnhancedDesired) return (false, null);", System.StringComparison.Ordinal);
            int write = body.IndexOf("WriteFlydigiHintUnderJoystickLock(next)", System.StringComparison.Ordinal);
            Assert.True(gate > 0 && write > gate, "the switch is read under the lock, before the write");
            int helper = im.IndexOf("private static bool WriteFlydigiHintUnderJoystickLock(string value)", System.StringComparison.Ordinal);
            Assert.True(helper > 0);
            string helperBody = im.Substring(helper, 500);
            int lockAt = helperBody.IndexOf("SDL_LockJoysticks();", System.StringComparison.Ordinal);
            int setAt = helperBody.IndexOf("SDL_SetHint(SDL_HINT_JOYSTICK_HIDAPI_FLYDIGI, value)", System.StringComparison.Ordinal);
            int unlockAt = helperBody.IndexOf("finally { SDL_UnlockJoysticks(); }", System.StringComparison.Ordinal);
            Assert.True(lockAt > 0 && setAt > lockAt && unlockAt > setAt, "lock, write, unlock in finally");
            int idle = im.IndexOf("if (_enumerationTimer.ElapsedMilliseconds >= 5000)", System.StringComparison.Ordinal);
            int normal = im.IndexOf("if (firstCycle || _enumerationTimer.ElapsedMilliseconds >= EnumerationIntervalMs)", System.StringComparison.Ordinal);
            Assert.True(idle > 0 && normal > 0);
            Assert.Contains("FlydigiReprobeTick();", im.Substring(idle, 400));
            Assert.Contains("FlydigiReprobeTick();", im.Substring(normal, 600));
            string step1 = File.ReadAllText(Path.Combine(RepoRoot(), "PadForge.App", "Common", "Input", "InputManager.Step1.UpdateDevices.cs"));
            int tick = step1.IndexOf("private void FlydigiReprobeTick()", System.StringComparison.Ordinal);
            Assert.True(tick > 0);
            string tickBody = step1.Substring(tick, 2600);
            Assert.Contains("SdlHidEnumeration.Paths(0x37D7, 0xFFA0)", tickBody);
            Assert.Contains("w.Backend == \"hidapi\"", tickBody);
            Assert.Contains("!w.IsAttached", tickBody);
            Assert.Contains("_flydigiReprobe.Observe(", tickBody);
            Assert.DoesNotContain("OnArrival(", step1);
        }

        [Fact]
        public void TheNudge_WritesNothingWhileTheSwitchIsOff_AndAlternatesWhileOn()
        {
            // SDL hints need no SDL_Init. The switch's recorded state is
            // restored afterward so the other tests see the default. SDL may
            // refuse the write when an environment variable outranks it: then
            // the recorded value stays put and both nudges request the same
            // string, which is the correct behavior for a refused write.
            bool before = PadForge.Common.Input.InputManager.FlydigiEnhancedProtocolDesired;
            try
            {
                PadForge.Common.Input.InputManager.ApplyFlydigiEnhancedProtocol(false);
                var off = PadForge.Common.Input.InputManager.TryFlydigiReprobeNudge();
                Assert.False(off.written);
                Assert.Null(off.value);
                PadForge.Common.Input.InputManager.ApplyFlydigiEnhancedProtocol(true);
                var first = PadForge.Common.Input.InputManager.TryFlydigiReprobeNudge();
                var second = PadForge.Common.Input.InputManager.TryFlydigiReprobeNudge();
                Assert.Equal(first.written, second.written);
                if (first.written)
                {
                    Assert.Equal("true", first.value);
                    Assert.Equal("1", second.value);
                }
                else
                {
                    Assert.Equal("true", first.value);
                    Assert.Equal("true", second.value);
                }
            }
            finally { PadForge.Common.Input.InputManager.ApplyFlydigiEnhancedProtocol(before); }
        }

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(System.AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln"))) d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }
    }
}
