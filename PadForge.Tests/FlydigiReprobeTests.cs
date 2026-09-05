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
    /// identity is the vendor interface's HID path, and SDL's HID change
    /// counter tells one connection from the next.
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
            Assert.Empty(p.Observe(1000, new[] { A }, None, false, deviceChanged: true));   // arrival: deadline set
            Assert.True(p.Armed);
            Assert.Empty(p.Observe(1000 + D - 1, new[] { A }, None, false, false));
            Assert.Equal(new[] { A }, p.Observe(1000 + D, new[] { A }, None, false, false));
            Assert.Equal(1, p.LastAttempt);
            Assert.Empty(p.Observe(1000 + D + 10, new[] { A }, None, false, false));
            Assert.Single(p.Observe(1000 + 2 * D, new[] { A }, None, false, false));
            Assert.Equal(2, p.LastAttempt);
        }

        [Fact]
        public void AClaimedInterface_IsNeverProbed_AndStaysDoneIfItsViewLaterGoes()
        {
            var p = new FlydigiReprobePolicy();
            Assert.Empty(p.Observe(0, new[] { A }, new[] { A.ToUpperInvariant() }, false, true));   // case-insensitive
            Assert.False(p.Armed);
            Assert.Empty(p.Observe(10 * D, new[] { A }, None, false, false));   // the view went, no device change: done
            Assert.False(p.Armed);
        }

        [Fact]
        public void TwoIdenticalPads_AreTwoPaths_AndOnlyTheUnclaimedOneIsProbed()
        {
            var p = new FlydigiReprobePolicy();
            p.Observe(0, new[] { A, B }, new[] { A }, false, true);
            Assert.Equal(new[] { B }, p.Observe(D, new[] { A, B }, new[] { A }, false, false));
            Assert.Empty(p.Observe(2 * D, new[] { A, B }, new[] { A, B }, false, false));   // B recovered
            Assert.False(p.Armed);
        }

        [Fact]
        public void ADeviceChange_StartsAPresentInterfaceOver_ClaimedOrExhausted()
        {
            // A reconnect at the same path between two observations: nothing
            // ever saw the path absent. The counter moved, so the interface is
            // a new connection and gets a fresh deadline and budget.
            var p = new FlydigiReprobePolicy();
            p.Observe(0, new[] { A }, new[] { A }, false, true);               // claimed
            Assert.False(p.Armed);
            Assert.Empty(p.Observe(1200, new[] { A }, None, false, deviceChanged: true));   // reconnected, probe failed
            Assert.True(p.Armed);
            Assert.Empty(p.Observe(1200 + D - 1, new[] { A }, None, false, false));
            Assert.Single(p.Observe(1200 + D, new[] { A }, None, false, false));
            // Exhausted, then a device change: fresh again.
            for (long t = 1200 + D; t <= 1200 + D * 12; t += 100) p.Observe(t, new[] { A }, None, false, false);
            Assert.False(p.Armed);
            p.Observe(50_000, new[] { A }, None, false, deviceChanged: true);
            Assert.True(p.Armed);
            Assert.Single(p.Observe(50_000 + D, new[] { A }, None, false, false));
            Assert.Equal(1, p.LastAttempt);
        }

        [Fact]
        public void ADeviceChange_LeavesAStillClaimedInterfaceClaimed()
        {
            var p = new FlydigiReprobePolicy();
            p.Observe(0, new[] { A, B }, new[] { A }, false, true);
            // B leaves (counter moves). A is re-read as claimed on the same observation.
            Assert.Empty(p.Observe(5000, new[] { A }, new[] { A }, false, deviceChanged: true));
            Assert.False(p.Armed);
            Assert.Equal(1, p.Tracked);
        }

        [Fact]
        public void AnAbsenceWithoutADeviceChange_IsAFlake_AndTheStateStands()
        {
            // A exhausted its budget. An enumeration that misses A while the
            // counter did not move must not forget A and renew the budget.
            var p = new FlydigiReprobePolicy();
            p.Observe(0, new[] { A }, None, false, true);
            for (long t = 0; t <= D * 12; t += 100) p.Observe(t, new[] { A }, None, false, false);
            Assert.False(p.Armed);
            Assert.Empty(p.Observe(100_000, None, None, false, deviceChanged: false));
            Assert.Equal(1, p.Tracked);
            Assert.Empty(p.Observe(100_000 + D, new[] { A }, None, false, false));
            Assert.False(p.Armed);
            // With the counter moved, absence is real.
            p.Observe(200_000, None, None, false, deviceChanged: true);
            Assert.Equal(0, p.Tracked);
        }

        [Fact]
        public void APadWithNoJoystickView_IsStillSeen()
        {
            // Presence comes from the HID enumeration, not from any wrapper.
            var p = new FlydigiReprobePolicy();
            p.Observe(0, new[] { A }, None, false, true);
            Assert.Single(p.Observe(D, new[] { A }, None, false, false));
        }

        [Fact]
        public void ASecondPadArriving_DoesNotRenewTheFirstPadsSpentBudget_UnlessTheCounterMoved()
        {
            var p = new FlydigiReprobePolicy();
            p.Observe(0, new[] { A }, None, false, true);
            for (long t = 0; t <= D * 12; t += 100) p.Observe(t, new[] { A }, None, false, false);
            Assert.False(p.Armed);
            // B appears in an observation with no counter move (the counter is
            // read before the enumeration): A stays spent, only B is armed.
            p.Observe(100_000, new[] { A, B }, None, false, deviceChanged: false);
            Assert.Equal(new[] { B }, p.Observe(100_000 + D, new[] { A, B }, None, false, false));
        }

        [Fact]
        public void FluxDefersEveryDeadline_SoAQuickReplugGetsItsFullDelay()
        {
            var p = new FlydigiReprobePolicy();
            p.Observe(0, new[] { A }, None, false, true);
            Assert.Empty(p.Observe(1200, new[] { A }, None, inFlux: true, deviceChanged: false));   // deferred to 2400
            Assert.Empty(p.Observe(1900, new[] { A }, None, inFlux: true, deviceChanged: false));   // deferred to 3100
            Assert.Empty(p.Observe(2400, new[] { A }, None, false, false));
            Assert.Single(p.Observe(3100, new[] { A }, None, false, false));
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
            // SDL_hid_device_info on x64 with SDL's eight-byte packing: 80
            // bytes, usage_page at 48, next at 72. A mismatch here would walk
            // garbage pointers.
            var (size, usagePage, next) = PadForge.Engine.SdlHidEnumeration.LayoutProbe();
            Assert.Equal(80, size);
            Assert.Equal(48, usagePage);
            Assert.Equal(72, next);
            // The native calls run on this bench: a null result is the failure
            // sentinel and fails here. An empty list is a legitimate result
            // (SDL enumerates controllers only by default, and there is no
            // Flydigi here).
            Assert.NotNull(PadForge.Engine.SdlHidEnumeration.DeviceChangeCount());
            var all = PadForge.Engine.SdlHidEnumeration.Paths(0, 0);
            Assert.NotNull(all);
            Assert.All(all, path => Assert.False(string.IsNullOrWhiteSpace(path)));
            Assert.NotNull(PadForge.Engine.SdlHidEnumeration.Paths(0x37D7, 0xFFA0));
        }

        [Fact]
        public void TheSwitchAndTheNudge_ShareOneLock_TakeTheJoystickLock_AndTheTickObservesOnChange()
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
            string tickBody = step1.Substring(tick, 3200);
            // Observe on a counter move, or on the delay cadence while retrying. Never otherwise.
            int countAt = tickBody.IndexOf("SdlHidEnumeration.DeviceChangeCount()", System.StringComparison.Ordinal);
            int gateAt = tickBody.IndexOf("if (!changed && !_flydigiReprobe.Armed) return;", System.StringComparison.Ordinal);
            int enumAt = tickBody.IndexOf("SdlHidEnumeration.Paths(0x37D7, 0xFFA0)", System.StringComparison.Ordinal);
            int nullAt = tickBody.IndexOf("if (present == null) return;", System.StringComparison.Ordinal);
            int observeAt = tickBody.IndexOf("_flydigiReprobe.Observe(now, present, claimed, inFlux, changed)", System.StringComparison.Ordinal);
            Assert.True(countAt > 0 && gateAt > countAt && enumAt > gateAt && nullAt > enumAt && observeAt > nullAt,
                "counter, gate, enumerate, null check, observe");
            Assert.Contains("!w.IsAttached", tickBody);
            Assert.Contains("w.Backend == \"hidapi\"", tickBody);
            Assert.DoesNotContain("anyV2", tickBody);              // discovery needs no wrapper
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
