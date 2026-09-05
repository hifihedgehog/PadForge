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
    /// first second by changing the hint's string and not its meaning.
    /// </summary>
    [Collection("FlydigiSwitchStatics")]
    public class FlydigiReprobeTests
    {
        private const int D = FlydigiReprobePolicy.DelayMs;

        [Fact]
        public void AnOrdinaryArrival_ArmsOneProbe_AfterTheDelay_SpacedNotPerTick()
        {
            var p = new FlydigiReprobePolicy();
            p.OnArrival(7, onHidapi: false, nowTicks: 1000);
            Assert.True(p.Armed);
            Assert.False(p.ShouldNudge(1000 + D - 1, 1, 0));    // not yet
            Assert.True(p.ShouldNudge(1000 + D, 1, 0));         // due
            Assert.Equal(1, p.LastAttempt);
            Assert.False(p.ShouldNudge(1000 + D + 10, 1, 0));   // spaced
            Assert.True(p.ShouldNudge(1000 + 2 * D, 1, 0));
            Assert.Equal(2, p.LastAttempt);
        }

        [Fact]
        public void AHidapiArrival_ArmsNothing_AndEveryPadHavingItsView_EndsRecovery()
        {
            var p = new FlydigiReprobePolicy();
            p.OnArrival(8, onHidapi: true, nowTicks: 0);
            Assert.False(p.Armed);
            p.OnArrival(7, false, 0);
            Assert.True(p.Armed);
            Assert.False(p.ShouldNudge(D, xinputViews: 1, enhancedViews: 1));   // every pad has its view
            Assert.False(p.Armed);
            // Startup recovery only: a later deficit for the same connection
            // does not restart it.
            Assert.False(p.ShouldNudge(10 * D, xinputViews: 1, enhancedViews: 0));
        }

        [Fact]
        public void OneHealthyPad_DoesNotHideASecondPadMissingItsView()
        {
            // Pad A: xinput 1 and hidapi 2. Pad B: xinput 3 only, its probe failed.
            var p = new FlydigiReprobePolicy();
            p.OnArrival(1, false, 0);
            p.OnArrival(2, true, 0);
            p.OnArrival(3, false, 10);
            Assert.True(p.ShouldNudge(D + 10, xinputViews: 2, enhancedViews: 1));     // one view short
            Assert.False(p.ShouldNudge(2 * D + 10, xinputViews: 2, enhancedViews: 2)); // B's view came up
            Assert.False(p.Armed);
        }

        [Fact]
        public void ASecondPadArriving_DoesNotRenewTheFirstPadsSpentBudget()
        {
            var p = new FlydigiReprobePolicy();
            p.OnArrival(1, false, 0);
            int fired = 0;
            for (long t = 0; t <= D * 12; t += 100)
                if (p.ShouldNudge(t, 1, 0)) fired++;
            Assert.Equal(FlydigiReprobePolicy.MaxAttempts, fired);
            Assert.False(p.Armed);
            // Pad B arrives. Only B's budget is live: four more, not eight.
            p.OnArrival(2, false, 100_000);
            Assert.True(p.Armed);
            fired = 0;
            for (long t = 100_000; t <= 100_000 + D * 12; t += 100)
                if (p.ShouldNudge(t, 2, 0)) fired++;
            Assert.Equal(FlydigiReprobePolicy.MaxAttempts, fired);
            Assert.Equal(FlydigiReprobePolicy.MaxAttempts, p.LastAttempt);
            Assert.False(p.Armed);
        }

        [Fact]
        public void TheLastOrdinaryViewLeaving_Disarms_AndUnknownIdsAreIgnored()
        {
            var p = new FlydigiReprobePolicy();
            p.OnArrival(1, false, 0);
            p.OnArrival(2, false, 0);
            p.OnDeparture(99);
            Assert.True(p.Armed);
            p.OnDeparture(1);
            Assert.True(p.Armed);
            p.OnDeparture(2);
            Assert.False(p.Armed);
            Assert.Equal(0, p.OrdinaryViews);
        }

        [Fact]
        public void NoPadOnXInput_Disarms()
        {
            var p = new FlydigiReprobePolicy();
            p.OnArrival(1, false, 0);
            Assert.False(p.ShouldNudge(D, xinputViews: 0, enhancedViews: 0));
            Assert.False(p.Armed);
        }

        [Fact]
        public void FourAttempts_ThenTheProbesStop_UntilANewConnection()
        {
            var p = new FlydigiReprobePolicy();
            p.OnArrival(1, false, 0);
            int fired = 0;
            for (long t = 0; t <= D * 12; t += 100)
                if (p.ShouldNudge(t, 1, 0)) fired++;
            Assert.Equal(FlydigiReprobePolicy.MaxAttempts, fired);
            Assert.False(p.Armed);
            // The same joystick id seen again re-arms nothing.
            p.OnArrival(1, false, 100_000);
            Assert.False(p.Armed);
            // A replug is a new joystick id: a fresh deadline and budget.
            p.OnDeparture(1);
            p.OnArrival(2, false, 100_000);
            Assert.True(p.Armed);
            Assert.False(p.ShouldNudge(100_000 + D - 1, 1, 0));
            Assert.True(p.ShouldNudge(100_000 + D, 1, 0));
            Assert.Equal(1, p.LastAttempt);
        }

        [Fact]
        public void AReconnectBetweenEnumerations_StartsAFreshDeadline()
        {
            // Old connection armed at 0. The pad left at 500 and came back as a
            // new SDL joystick, opened at 2100. The probe must not fire at 2100
            // on the old deadline, 200 ms into the new connection.
            var p = new FlydigiReprobePolicy();
            p.OnArrival(1, false, 0);
            p.OnDeparture(1);
            p.OnArrival(2, false, 2100);
            Assert.False(p.ShouldNudge(2100, 1, 0));
            Assert.False(p.ShouldNudge(2100 + D - 1, 1, 0));
            Assert.True(p.ShouldNudge(2100 + D, 1, 0));
        }

        [Fact]
        public void TheSameJoystickIdSeenAgain_DoesNotResetTheClock()
        {
            var p = new FlydigiReprobePolicy();
            p.OnArrival(1, false, 0);
            p.OnArrival(1, false, 900);
            Assert.True(p.ShouldNudge(D, 1, 0));
        }

        [Theory]
        [InlineData("1", "true")]
        [InlineData("true", "1")]
        [InlineData("0", "1")]
        [InlineData(null, "1")]
        public void TheHintString_Alternates_AndAlwaysMeansOn(string current, string expected)
            => Assert.Equal(expected, FlydigiReprobePolicy.NextHintValue(current));

        [Fact]
        public void TheSwitchAndTheNudge_ShareOneLock_TakeTheJoystickLock_AndTheTickRunsOnBothLoops()
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
            // The write itself holds SDL's joystick lock, so a concurrent
            // HIDAPI re-evaluation on the UI pump cannot clear the change
            // flag out from under it.
            int helper = im.IndexOf("private static bool WriteFlydigiHintUnderJoystickLock(string value)", System.StringComparison.Ordinal);
            Assert.True(helper > 0);
            string helperBody = im.Substring(helper, 500);
            Assert.True(helperBody.IndexOf("SDL_LockJoysticks();", System.StringComparison.Ordinal)
                        < helperBody.IndexOf("SDL_SetHint(SDL_HINT_JOYSTICK_HIDAPI_FLYDIGI, value)", System.StringComparison.Ordinal));
            Assert.Contains("finally { SDL_UnlockJoysticks(); }", helperBody);
            // The tick follows both enumeration gates, so it runs every poll
            // cycle and not on the 2 s or 5 s enumeration cadence.
            int idle = im.IndexOf("if (_enumerationTimer.ElapsedMilliseconds >= 5000)", System.StringComparison.Ordinal);
            int normal = im.IndexOf("if (firstCycle || _enumerationTimer.ElapsedMilliseconds >= EnumerationIntervalMs)", System.StringComparison.Ordinal);
            Assert.True(idle > 0 && normal > 0);
            Assert.Contains("FlydigiReprobeTick();", im.Substring(idle, 400));
            Assert.Contains("FlydigiReprobeTick();", im.Substring(normal, 600));
            string step1 = File.ReadAllText(Path.Combine(RepoRoot(), "PadForge.App", "Common", "Input", "InputManager.Step1.UpdateDevices.cs"));
            Assert.Contains("_flydigiReprobe.OnArrival(wrapper.SdlInstanceId, wrapper.Backend == \"hidapi\", Environment.TickCount64);", step1);
            Assert.Contains("_flydigiReprobe.OnDeparture(sdlId);", step1);
            // The census skips detached wrappers awaiting cleanup and counts
            // V2 pads only, the vendor SDL keeps an XInput view alongside.
            int tick = step1.IndexOf("private void FlydigiReprobeTick()", System.StringComparison.Ordinal);
            Assert.True(tick > 0);
            string tickBody = step1.Substring(tick, 1600);
            Assert.Contains("!w.IsAttached", tickBody);
            Assert.Contains("w.VendorId != 0x37D7", tickBody);
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
                    Assert.Equal("true", first.value);      // "1" was just written by the switch
                    Assert.Equal("1", second.value);
                }
                else
                {
                    Assert.Equal("true", first.value);      // the cache did not move on a refused write
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
