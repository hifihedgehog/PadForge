using System.Collections.Generic;
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
    /// identity is the vendor interface's HID path, and generations come from
    /// Flydigi wrapper identity.
    /// </summary>
    [Collection("FlydigiSwitchStatics")]
    public class FlydigiReprobeTests
    {
        private const int D = FlydigiReprobePolicy.DelayMs;
        private const string A = @"\\?\HID#VID_37D7&PID_2401&MI_01#7&2e838171&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        private const string B = @"\\?\HID#VID_37D7&PID_2401&MI_01#7&0abc0abc&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        private static readonly string[] None = new string[0];
        private static readonly (string, uint)[] NoClaims = new (string, uint)[0];
        private static readonly uint[] NoIds = new uint[0];

        private static IReadOnlyList<string> Obs(FlydigiReprobePolicy p, long t, string[] present,
            (string, uint)[] claimed = null, uint[] attached = null, bool flux = false, bool real = false)
            => p.Observe(t, present, claimed ?? NoClaims, attached ?? NoIds, flux, real);

        [Fact]
        public void AnUnclaimedInterface_IsProbedAfterTheDelay_ThenSpaced()
        {
            var p = new FlydigiReprobePolicy();
            // The pad's XInput view (id 7) is attached: a connection event on
            // first sight, which sets the deadline one delay out.
            Assert.Empty(Obs(p, 1000, new[] { A }, attached: new uint[] { 7 }, real: true));
            Assert.True(p.Armed);
            Assert.Empty(Obs(p, 1000 + D - 1, new[] { A }, attached: new uint[] { 7 }));
            Assert.Equal(new[] { A }, Obs(p, 1000 + D, new[] { A }, attached: new uint[] { 7 }));
            Assert.Equal(1, p.LastAttempt);
            Assert.Empty(Obs(p, 1000 + D + 10, new[] { A }, attached: new uint[] { 7 }));
            Assert.Single(Obs(p, 1000 + 2 * D, new[] { A }, attached: new uint[] { 7 }));
            Assert.Equal(2, p.LastAttempt);
        }

        [Fact]
        public void AClaimedInterface_IsNeverProbed_WhileItsClaimingWrapperIsAttached()
        {
            var p = new FlydigiReprobePolicy();
            Assert.Empty(Obs(p, 0, new[] { A }, new[] { (A.ToUpperInvariant(), 8u) }, new uint[] { 7, 8 }, real: true));
            Assert.False(p.Armed);
            Assert.Empty(Obs(p, 10 * D, new[] { A }, new[] { (A, 8u) }, new uint[] { 7, 8 }));
            Assert.False(p.Armed);
        }

        [Fact]
        public void AClaimGoesStale_WhenItsWrapperIsGone_AndThePathStays()
        {
            // A reconnect at the same path: the old enhanced wrapper (8) is
            // cleaned up, the path is still present, the new connection's
            // probe failed. The interface starts over with a fresh deadline.
            var p = new FlydigiReprobePolicy();
            Obs(p, 0, new[] { A }, new[] { (A, 8u) }, new uint[] { 7, 8 }, real: true);
            Assert.False(p.Armed);
            Assert.Empty(Obs(p, 5000, new[] { A }, attached: new uint[] { 9 }));      // 8 gone, 9 is the new XInput view
            Assert.True(p.Armed);
            Assert.Empty(Obs(p, 5000 + D - 1, new[] { A }, attached: new uint[] { 9 }));
            Assert.Single(Obs(p, 5000 + D, new[] { A }, attached: new uint[] { 9 }));
            Assert.Equal(1, p.LastAttempt);
        }

        [Fact]
        public void ANewFlydigiJoystickId_RenewsAnUnclaimedInterfacesBudget()
        {
            // A exhausted its budget. A joystick id never seen before means a
            // pad arrived or came back: A gets four more, one delay out.
            var p = new FlydigiReprobePolicy();
            Obs(p, 0, new[] { A }, attached: new uint[] { 7 }, real: true);
            for (long t = 0; t <= D * 12; t += 100) Obs(p, t, new[] { A }, attached: new uint[] { 7 });
            Assert.False(p.Armed);
            Assert.Empty(Obs(p, 100_000, new[] { A }, attached: new uint[] { 11 }));
            Assert.True(p.Armed);
            Assert.Empty(Obs(p, 100_000 + D - 1, new[] { A }, attached: new uint[] { 11 }));
            Assert.Single(Obs(p, 100_000 + D, new[] { A }, attached: new uint[] { 11 }));
            Assert.Equal(1, p.LastAttempt);
            // The same id again is not an event.
            for (long t = 100_000 + D; t <= 100_000 + D * 12; t += 100) Obs(p, t, new[] { A }, attached: new uint[] { 11 });
            Assert.False(p.Armed);
            Assert.Empty(Obs(p, 200_000, new[] { A }, attached: new uint[] { 11 }));
            Assert.False(p.Armed);
        }

        [Fact]
        public void TwoIdenticalPads_AreTwoPaths_AndOnlyTheUnclaimedOneIsProbed()
        {
            var p = new FlydigiReprobePolicy();
            Obs(p, 0, new[] { A, B }, new[] { (A, 2u) }, new uint[] { 1, 2, 3 }, real: true);
            Assert.Equal(new[] { B }, Obs(p, D, new[] { A, B }, new[] { (A, 2u) }, new uint[] { 1, 2, 3 }));
            // B recovered: its enhanced wrapper (4) is a new id, a connection
            // event, but B is claimed now and A stays claimed.
            Assert.Empty(Obs(p, 2 * D, new[] { A, B }, new[] { (A, 2u), (B, 4u) }, new uint[] { 1, 2, 3, 4 }));
            Assert.False(p.Armed);
        }

        [Fact]
        public void ACounterMove_ResetsNothing_ItOnlyMakesAbsencesReal()
        {
            // Unrelated notifications every second must not starve the deadline
            // or renew a spent budget.
            var p = new FlydigiReprobePolicy();
            Obs(p, 0, new[] { A }, attached: new uint[] { 7 }, real: true);
            Assert.Empty(Obs(p, 1000, new[] { A }, attached: new uint[] { 7 }, real: true));
            Assert.Single(Obs(p, D, new[] { A }, attached: new uint[] { 7 }, real: true));       // due on time
            for (long t = D; t <= D * 12; t += 100) Obs(p, t, new[] { A }, attached: new uint[] { 7 }, real: true);
            Assert.False(p.Armed);
            Assert.Empty(Obs(p, 100_000, new[] { A }, attached: new uint[] { 7 }, real: true));  // no renewal
            Assert.False(p.Armed);
            // Absent while the counter moved: gone. Absent without: a flake.
            Obs(p, 200_000, None, attached: new uint[] { 7 });
            Assert.Equal(1, p.Tracked);
            Obs(p, 200_100, None, attached: new uint[] { 7 }, real: true);
            Assert.Equal(0, p.Tracked);
        }

        [Fact]
        public void APadWithNoJoystickView_IsStillSeen_AndProbed()
        {
            // Presence comes from the HID enumeration. With no wrapper there is
            // no connection event, so the deadline is one delay from first sight.
            var p = new FlydigiReprobePolicy();
            Obs(p, 0, new[] { A }, real: true);
            Assert.Empty(Obs(p, D - 1, new[] { A }));
            Assert.Single(Obs(p, D, new[] { A }));
        }

        [Fact]
        public void FluxDefersEveryDeadline_SoAQuickReplugGetsItsFullDelay()
        {
            var p = new FlydigiReprobePolicy();
            Obs(p, 0, new[] { A }, attached: new uint[] { 7 }, real: true);
            Assert.Empty(Obs(p, 1200, new[] { A }, attached: new uint[] { 7 }, flux: true));    // deferred to 2400
            Assert.Empty(Obs(p, 1900, new[] { A }, attached: new uint[] { 7 }, flux: true));    // deferred to 3100
            Assert.Empty(Obs(p, 2400, new[] { A }, attached: new uint[] { 7 }));
            Assert.Single(Obs(p, 3100, new[] { A }, attached: new uint[] { 7 }));
        }

        [Theory]
        [InlineData("1", "true")]
        [InlineData("true", "1")]
        [InlineData("0", "1")]
        [InlineData(null, "1")]
        public void TheHintString_Alternates_AndAlwaysMeansOn(string current, string expected)
            => Assert.Equal(expected, FlydigiReprobePolicy.NextHintValue(current));

        [Fact]
        public void SdlHidEnumeration_LayoutMatchesSdl_AndTheNativeCallsRun()
        {
            var (size, usagePage, next) = PadForge.Engine.SdlHidEnumeration.LayoutProbe();
            Assert.Equal(80, size);
            Assert.Equal(48, usagePage);
            Assert.Equal(72, next);
            Assert.NotNull(PadForge.Engine.SdlHidEnumeration.DeviceChangeCount());
            var all = PadForge.Engine.SdlHidEnumeration.Paths(0, 0);
            Assert.NotNull(all);
            Assert.All(all, path => Assert.False(string.IsNullOrWhiteSpace(path)));
            Assert.NotNull(PadForge.Engine.SdlHidEnumeration.Paths(0x37D7, 0xFFA0));
        }

        [Fact]
        public void TheSwitchAndTheNudge_ShareOneLock_TakeTheJoystickLock_AndTheTickObservesInOrder()
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
            string tickBody = step1.Substring(tick, 3600);
            // Counter, gates, enumerate, null check, THEN the wrapper snapshot, then observe.
            int countAt = tickBody.IndexOf("SdlHidEnumeration.DeviceChangeCount()", System.StringComparison.Ordinal);
            int gateAt = tickBody.IndexOf("if (!changed && !confirm && !_flydigiReprobe.Armed) return;", System.StringComparison.Ordinal);
            int enumAt = tickBody.IndexOf("SdlHidEnumeration.Paths(0x37D7, 0xFFA0)", System.StringComparison.Ordinal);
            int nullAt = tickBody.IndexOf("if (present == null) return;", System.StringComparison.Ordinal);
            int snapAt = tickBody.IndexOf("foreach (var w in _openedSdlInstanceIds.Values)", System.StringComparison.Ordinal);
            int confirmAt = tickBody.IndexOf("_flydigiConfirmDue = changed ? now + FlydigiReprobePolicy.DelayMs : 0;", System.StringComparison.Ordinal);
            int observeAt = tickBody.IndexOf("_flydigiReprobe.Observe(now, present, claimed, attached, inFlux, absencesAreReal: changed)", System.StringComparison.Ordinal);
            Assert.True(countAt > 0 && gateAt > countAt && enumAt > gateAt && nullAt > enumAt && snapAt > nullAt && confirmAt > snapAt && observeAt > confirmAt,
                "counter, gate, enumerate, null check, snapshot after the enumeration, confirmation scheduled, observe");
            Assert.Contains("!w.IsAttached", tickBody);
            Assert.Contains("claimed.Add((w.DevicePath, w.SdlInstanceId))", tickBody);
            Assert.DoesNotContain("OnArrival(", step1);
        }

        [Fact]
        public void TheNudge_WritesNothingWhileTheSwitchIsOff_AndAlternatesWhileOn()
        {
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
