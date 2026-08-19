using System;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// #334: the DualSense lightbar stopped following the Lighting tab and
    /// sat on its firmware default blue.
    ///
    /// <para>The identity writer yields the bar to the pass-through while
    /// the pass-through holds it. It asked
    /// <c>IsHoldingState</c>, which is true whenever the lane has forwarded
    /// ANY effect payload: adaptive triggers, rumble, audio, the player
    /// LED. A host driving triggers and never touching the bar therefore
    /// took the lightbar away from the user for the whole 15 s idle
    /// window, and any host that keeps talking holds it indefinitely, which
    /// is why toggling the engine did not always give it back. The pips and
    /// the mic LED gate through GateMirroredSubsystem instead, so they kept
    /// working, which is what made it read as "only the lightbar".</para>
    ///
    /// <para>The distinction under test: driving the LANE is not driving the
    /// BAR. Only a payload that asserts validFlag1 bit 2 hands the lightbar
    /// over, and the #300 flashing fix still depends on that case holding
    /// for the full window.</para>
    /// </summary>
    // Seeds SettingsManager's device/settings stores, so it rides the same
    // collection as every other test that touches those statics and restores
    // them in Dispose. A shared static mutated without both is the recorded
    // cause of this suite's flakes.
    [Collection("SettingsManagerStatics")]
    public class LightbarPassthroughHoldTests : IDisposable
    {
        private const int Slot = 6;
        private readonly DualSensePassthroughDispatcher _d;
        private readonly DeviceCollection _savedDevices;
        private readonly SettingsCollection _savedSettings;

        public LightbarPassthroughHoldTests()
        {
            _savedDevices = SettingsManager.UserDevices;
            _savedSettings = SettingsManager.UserSettings;
            _d = new DualSensePassthroughDispatcher(Slot);
            // The real path: the worker is what sets the lane's driving
            // state, and HoldingLightbar deliberately requires it. With no
            // device assigned to this slot DispatchOne resolves zero targets
            // and returns, so the worker touches no hardware.
            _d.Start();
        }

        public void Dispose()
        {
            _d.Dispose();
            SettingsManager.UserDevices = _savedDevices;
            SettingsManager.UserSettings = _savedSettings;
        }

        /// <summary>Waits for the worker to have processed a payload. Bounded,
        /// so a wedged worker fails the test instead of hanging it.</summary>
        private static void WaitForLaneDriving(int timeoutMs = 4000)
        {
            long deadline = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < deadline)
            {
                if (DualSensePassthroughDispatcher.IsHoldingState(Slot)) return;
                System.Threading.Thread.Sleep(15);
            }
        }

        /// <summary>Effect payload of the shape the mirror decodes:
        /// payload[0] validFlag0, payload[1] validFlag1, RGB at 44..46.</summary>
        private static byte[] Payload(byte validFlag1, byte r = 0, byte g = 0, byte b = 0)
        {
            var p = new byte[47];
            p[1] = validFlag1;
            p[44] = r; p[45] = g; p[46] = b;
            return p;
        }

        // validFlag1 bit 0 = rumble/mic lane, bit 2 = lightbar, bit 4 = pips.
        private const byte Vf1Lightbar = 0x04;
        private const byte Vf1NoLightbar = 0x01 | 0x10;

        /// <summary>THE REGRESSION. A host that drives the lane without ever
        /// asserting the lightbar must not take the bar. Before the fix this
        /// returned true and the Lighting tab was inert for 15 s at a time,
        /// renewed by every further packet.</summary>
        [Fact]
        public void EffectTrafficWithoutALightbarWrite_DoesNotHoldTheBar()
        {
            _d.Enqueue(0x02, Payload(Vf1NoLightbar));
            WaitForLaneDriving();

            Assert.False(DualSensePassthroughDispatcher.IsHoldingLightbar(Slot),
                "a payload that never asserts the lightbar enable bit must leave "
                + "the bar with the user (#334)");
        }

        /// <summary>The #300 case, unchanged: a game actually driving the bar
        /// holds it, so the identity writer stands down and the two writers
        /// stop alternating.</summary>
        [Fact]
        public void ALightbarWrite_HoldsTheBar()
        {
            _d.Enqueue(0x02, Payload(Vf1Lightbar, 255, 0, 0));
            WaitForLaneDriving();

            Assert.True(DualSensePassthroughDispatcher.IsHoldingLightbar(Slot),
                "a payload asserting the lightbar enable bit hands the bar to "
                + "the pass-through for its hold window (#300)");
        }

        /// <summary>Positive control for the test above: the lane IS driving
        /// state in the no-lightbar case, so the first test is measuring the
        /// per-subsystem distinction and not an inert dispatcher.</summary>
        [Fact]
        public void TheLaneIsDrivingStateEvenWhenItDoesNotDriveTheBar()
        {
            _d.Enqueue(0x02, Payload(Vf1NoLightbar));
            WaitForLaneDriving();

            Assert.True(DualSensePassthroughDispatcher.IsHoldingState(Slot),
                "the lane must be holding state, or the lightbar assertion "
                + "above proves nothing");
        }

        /// <summary>Mixed traffic: once the bar has been driven, later
        /// non-bar packets do not revoke the hold inside the window.</summary>
        [Fact]
        public void NonBarTrafficAfterABarWrite_KeepsTheHold()
        {
            _d.Enqueue(0x02, Payload(Vf1Lightbar, 0, 255, 0));
            _d.Enqueue(0x02, Payload(Vf1NoLightbar));
            WaitForLaneDriving();

            Assert.True(DualSensePassthroughDispatcher.IsHoldingLightbar(Slot));
        }

        /// <summary>An unknown slot never holds anything: the identity writer
        /// asks per slot and a missing dispatcher must not read as a claim.</summary>
        [Fact]
        public void ASlotWithNoDispatcher_HoldsNothing()
        {
            Assert.False(DualSensePassthroughDispatcher.IsHoldingLightbar(Slot + 1));
            Assert.False(DualSensePassthroughDispatcher.IsHoldingState(Slot + 1));
        }

        // ── The DECISION, not just the predicates ──
        //
        // The helper tests above all stayed green when the call site was
        // mutated back to IsHoldingState, because they never exercised the
        // call site. #334 lives exactly there: the predicates were both
        // fine, the question asked of them was wrong. These drive
        // ShouldAssertLightbar with a real mapped online DualSense so the
        // mutation that reproduces the regression turns them red.

        private static readonly Guid PadGuid = new("6a1d4f10-3f5e-4a2b-9c7d-334334334334");

        /// <summary>Seeds the stores IsPassthroughTarget reads: an online
        /// standard DualSense mapped to this slot.</summary>
        private static void SeedMappedDualSense()
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            lock (SettingsManager.UserDevices.SyncRoot)
                SettingsManager.UserDevices.Items.Add(new UserDevice
                {
                    InstanceGuid = PadGuid,
                    IsOnline = true,
                    VendorId = 0x054C,
                    ProdId = 0x0CE6,
                });
            lock (SettingsManager.UserSettings.SyncRoot)
                SettingsManager.UserSettings.Items.Add(new UserSetting
                {
                    InstanceGuid = PadGuid,
                    MapTo = Slot,
                });
        }

        /// <summary>THE REGRESSION AT ITS CALL SITE. Effect traffic that never
        /// touches the bar must leave PadForge asserting the lightbar, on a
        /// pad that IS a pass-through target. Mutating the call site back to
        /// IsHoldingState fails this.</summary>
        [Fact]
        public void Decision_NonBarTrafficOnAPassthroughTarget_StillAssertsTheLightbar()
        {
            SeedMappedDualSense();
            _d.Enqueue(0x02, Payload(Vf1NoLightbar));
            WaitForLaneDriving();

            Assert.True(
                DualSensePassthroughDispatcher.IsPassthroughTarget(Slot, PadGuid),
                "fixture must make this pad a pass-through target, or the "
                + "assertion below passes for the wrong reason");
            Assert.True(
                UserEffectsDispatcher.ShouldAssertLightbar(
                    Slot, PadGuid, gameDrivenBar: false, isDs5: true),
                "a host driving triggers or rumble and never the bar must not "
                + "take the Lighting tab's lightbar away (#334)");
        }

        /// <summary>The #300 stand-down still happens when the game really is
        /// driving the bar.</summary>
        [Fact]
        public void Decision_ABarWriteOnAPassthroughTarget_StandsDown()
        {
            SeedMappedDualSense();
            _d.Enqueue(0x02, Payload(Vf1Lightbar, 0, 0, 255));
            WaitForLaneDriving();

            Assert.False(
                UserEffectsDispatcher.ShouldAssertLightbar(
                    Slot, PadGuid, gameDrivenBar: false, isDs5: true),
                "a game driving the bar owns it for the hold window (#300)");
        }

        /// <summary>The mirrored 1.5 s grace still stands the writer down on
        /// its own, independent of the lane's hold.</summary>
        [Fact]
        public void Decision_MirroredBarWrite_StandsDown()
        {
            SeedMappedDualSense();

            Assert.False(
                UserEffectsDispatcher.ShouldAssertLightbar(
                    Slot, PadGuid, gameDrivenBar: true, isDs5: true),
                "an in-grace external bar write still yields the subsystem");
        }

        /// <summary>A DS4 is never suppressed by this gate: the suppression is
        /// scoped to the DS5 pass-through lane.</summary>
        [Fact]
        public void Decision_NonDs5_NeverSuppressed()
        {
            SeedMappedDualSense();
            _d.Enqueue(0x02, Payload(Vf1Lightbar, 255, 255, 0));
            WaitForLaneDriving();

            Assert.True(
                UserEffectsDispatcher.ShouldAssertLightbar(
                    Slot, PadGuid, gameDrivenBar: true, isDs5: false));
        }

        /// <summary>A payload too short to carry validFlag1 must not be read
        /// as a lightbar claim (and must not throw).</summary>
        [Fact]
        public void AShortPayload_IsNotALightbarClaim()
        {
            _d.Enqueue(0x02, new byte[] { 0x00 });

            Assert.False(DualSensePassthroughDispatcher.IsHoldingLightbar(Slot));
        }
    }
}
