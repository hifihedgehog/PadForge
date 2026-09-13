using System;
using System.Threading.Channels;
using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Guards for the DualSense effect lane's queueing contract (#300).
    ///
    /// The lane forwards game-written effect payloads to the physical pad and
    /// rents every payload from ArrayPool. That makes "what happens to an item
    /// the queue refuses" a memory-correctness question, not a throughput one,
    /// and the answer was got wrong on a belief about Channel semantics that
    /// nothing checked.
    /// </summary>
    public class DualSenseEffectLaneTests
    {
        // ── The belief that caused the leak ──
        //
        // The dispatcher used a bounded channel with FullMode.DropWrite and a
        // comment stating that TryWrite returns FALSE on overflow, so the
        // producer could return its rented buffer. It does not. Every Drop
        // mode accepts the write, reports success, and discards the item, so
        // the rental was never handed back to anyone.
        //
        // A field trace measured the cost: a title driving this lane at about
        // 18,000 packets per second showed roughly 10,500 per second
        // unaccounted for between what was enqueued and what was either
        // coalesced or written, with the drop counter reading zero throughout.
        // Those were the leaked rentals.
        //
        // This test pins the real semantics so the belief cannot come back.

        [Fact]
        public void DropWrite_AcceptsTheWriteAndDiscardsIt_SoAPooledPayloadWouldLeak()
        {
            var ch = Channel.CreateBounded<int>(new BoundedChannelOptions(2)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
                SingleWriter = false,
            });

            Assert.True(ch.Writer.TryWrite(1));
            Assert.True(ch.Writer.TryWrite(2));

            // The channel is full. This is the assertion that matters: the
            // write is REFUSED in effect but REPORTED as accepted, so a
            // producer keying its cleanup off the return value never cleans up.
            Assert.True(ch.Writer.TryWrite(3));

            Assert.True(ch.Reader.TryRead(out int first));
            Assert.True(ch.Reader.TryRead(out int second));
            Assert.False(ch.Reader.TryRead(out _));
            Assert.Equal(1, first);
            Assert.Equal(2, second);   // 3 was swallowed, never delivered
        }

        // ── Two reports, one state: a waiting frame merges by valid flags ──
        //
        // GTA V Enhanced (#434) writes its state as two reports back to back
        // every 10 ms: a rumble frame (valid_flag0 0x03 with the motor bytes)
        // and a trigger frame (0x0E with both trigger blocks, motors zero and
        // unflagged). The pad applies each report's flagged fields only, so
        // the two are one state and direct USB delivers both. A latch that
        // kept only the newest frame dropped one of every pair, and when the
        // dropped one was the game's final zero-motor rumble frame the pad
        // never received its stop. The latch now merges: the newer frame's
        // fields win, the older frame's flagged fields the newer did not carry
        // survive with their bits, and the written frame is the state the pad
        // would hold after applying both in order.
        //
        // The two #300 lessons hold under merging. A burst of repeats cannot
        // evict a waiting change, because a repeat carries the fields the pad
        // already holds and merging it leaves the change's fields in place.
        // A repeat with nothing waiting is still latched and written, so it
        // keeps re-asserting the game's state over the 30 Hz pass.

        private static byte[] RumbleFrame(byte left, byte right = 0)
        {
            var p = new byte[47];
            p[0] = 0x03; p[1] = 0x40; p[2] = right; p[3] = left;
            return p;
        }

        private static byte[] TriggerFrame()
        {
            var p = new byte[47];
            p[0] = 0x0E; p[1] = 0x40;
            p[10] = 0x02; p[11] = 0x20; p[12] = 0x60; p[13] = 0x80;
            p[21] = 0x02; p[22] = 0x20; p[23] = 0x60; p[24] = 0x80;
            return p;
        }

        private static byte[] Merge(byte[] older, byte[] newer)
        {
            var dest = new byte[Math.Max(older.Length, newer.Length)];
            int n = DualSensePassthroughDispatcher.MergeEffectPayload(older, newer, dest);
            Assert.Equal(dest.Length, n);
            return dest;
        }

        [Fact]
        public void TriggerFrameAfterRumbleFrame_KeepsTheMotors()
        {
            var m = Merge(RumbleFrame(145), TriggerFrame());
            Assert.Equal(0x0F, m[0]);
            Assert.Equal(145, m[3]);
            Assert.Equal(0x02, m[10]);
            Assert.Equal(0x80, m[24]);
        }

        [Fact]
        public void RumbleFrameAfterTriggerFrame_KeepsTheTriggers()
        {
            var m = Merge(TriggerFrame(), RumbleFrame(120));
            Assert.Equal(0x0F, m[0]);
            Assert.Equal(120, m[3]);
            Assert.Equal(0x02, m[10]);
            Assert.Equal(0x02, m[21]);
        }

        [Fact]
        public void TheFinalZeroMotorFrame_SurvivesTheTriggerFrameBehindIt()
        {
            // The #434 stop. Dropping this frame left the pad at its last
            // nonzero value with nothing left to clear it.
            var m = Merge(RumbleFrame(0), TriggerFrame());
            Assert.Equal(0x01, m[0] & 0x01);
            Assert.Equal(0, m[2]);
            Assert.Equal(0, m[3]);
        }

        [Fact]
        public void TheNewerFrameWins_WhereBothCarryAField()
        {
            var m = Merge(RumbleFrame(145), RumbleFrame(120));
            Assert.Equal(0x03, m[0]);
            Assert.Equal(120, m[3]);
        }

        [Fact]
        public void ARepeatMergedIntoAChange_LeavesTheChangeInPlace()
        {
            // The #300 eviction guard in its new form: the pad holds the
            // trigger frame, the rumble change waits, and a repeat of the
            // trigger frame arrives. The change's motors survive the merge.
            var m = Merge(RumbleFrame(145), TriggerFrame());
            Assert.Equal(145, m[3]);
            Assert.Equal(0x01, m[0] & 0x01);
        }

        [Fact]
        public void AFrameWithoutHapticsSelect_EndsRumbleAndDropsTheOlderMotors()
        {
            // The stop as SDL3 sends it and as the bench measured it: a
            // frame that does not select rumble puts the pad on audio
            // haptics. Restoring the older motors into it would keep the
            // pad rumbling past the game's stop.
            var stop = new byte[47];
            stop[0] = 0x0C; stop[10] = 0x02; stop[21] = 0x02;
            var m = Merge(RumbleFrame(145), stop);
            Assert.Equal(0x0C, m[0]);
            Assert.Equal(0, m[3]);
            var full = new byte[47];   // SDL3's own stop, every bit clear
            var m2 = Merge(RumbleFrame(145), full);
            Assert.Equal(0x00, m2[0]);
            var improvedOlder = new byte[47];
            improvedOlder[0] = 0x02; improvedOlder[38] = 0x04; improvedOlder[3] = 50;
            var m3 = Merge(improvedOlder, full);
            Assert.Equal(0x00, m3[0]);
            Assert.Equal(0x00, m3[38] & 0x04);
        }

        [Fact]
        public void ImprovedRumbleMotors_SurviveAFrameWithoutThem()
        {
            var older = new byte[47];
            older[0] = 0x02; older[38] = 0x04; older[3] = 50;
            var m = Merge(older, TriggerFrame());
            Assert.Equal(0x04, m[38] & 0x04);
            Assert.Equal(50, m[3]);
        }

        [Fact]
        public void ALightbarAndPlayerLeds_SurviveARumbleFrame()
        {
            var older = new byte[47];
            older[1] = 0x14; older[43] = 0x1F; older[44] = 1; older[45] = 2; older[46] = 3;
            var m = Merge(older, RumbleFrame(10));
            Assert.Equal(0x40 | 0x14, m[1]);
            Assert.Equal(0x1F, m[43]);
            Assert.Equal(1, m[44]);
            Assert.Equal(3, m[46]);
        }

        [Fact]
        public void MotorPowerByte_FollowsItsFlag()
        {
            var older = new byte[47];
            older[1] = 0x40; older[36] = 0x33;
            var newer = new byte[47];
            newer[0] = 0x0C; newer[10] = 0x02;
            var m = Merge(older, newer);
            Assert.Equal(0x40, m[1] & 0x40);
            Assert.Equal(0x33, m[36]);
        }

        [Fact]
        public void AnEdgeTail_ComesFromWhicheverFrameHasIt()
        {
            var older = new byte[63];
            older[0] = 0x03; older[3] = 9; older[62] = 0xAB;
            var m = Merge(older, TriggerFrame());
            Assert.Equal(63, m.Length);
            Assert.Equal(0xAB, m[62]);
            Assert.Equal(9, m[3]);
            var m2 = Merge(TriggerFrame(), older);
            Assert.Equal(63, m2.Length);
            Assert.Equal(0xAB, m2[62]);
            Assert.Equal(0x02, m2[10]);
        }

        [Fact]
        public void ARepeatOfWhatThePadHolds_IsRecognized()
        {
            var lastSent = new byte[] { 0x02, 0x11, 0x22, 0x33 };
            var incoming = new byte[] { 0x02, 0x11, 0x22, 0x33 };
            Assert.True(DualSensePassthroughDispatcher.IsRepeatOfLastSent(
                incoming, lastSent, lastSent.Length));
        }

        [Fact]
        public void AGenuineChange_IsNotARepeat()
        {
            var lastSent = new byte[] { 0x02, 0x11, 0x22, 0x33 };
            var changed = new byte[] { 0x02, 0x11, 0x22, 0x34 };
            Assert.False(DualSensePassthroughDispatcher.IsRepeatOfLastSent(
                changed, lastSent, lastSent.Length));
        }

        [Fact]
        public void ADifferentLength_IsNeverARepeat()
        {
            // Edge and standard pads carry different payload lengths, and a
            // shorter payload that happens to prefix-match is a different
            // message, not the same one.
            var lastSent = new byte[] { 0x02, 0x11, 0x22, 0x33 };
            var shorter = new byte[] { 0x02, 0x11, 0x22 };
            Assert.False(DualSensePassthroughDispatcher.IsRepeatOfLastSent(
                shorter, lastSent, lastSent.Length));
        }

        [Fact]
        public void BeforeAnythingHasBeenSent_NothingIsARepeat()
        {
            // The first payload of a session must always go out, or the pad
            // keeps whatever state it powered on with.
            Assert.False(DualSensePassthroughDispatcher.IsRepeatOfLastSent(
                new byte[] { 1, 2, 3 }, null, 0));
        }

        // ── Handing the pad back when the game goes away ──
        //
        // A physical DualSense holds its adaptive trigger program in firmware
        // until something loads a different one, so a game that exits mid-effect
        // leaves it there (#300, two reporters). Nothing announces a departure:
        // ViGEmClient's notifications are output reports only and VIIPER has no
        // equivalent, so every tool doing this job uses a staleness window.
        // DualSenseY-v2, which drives a physical DualSense from a game's DSX
        // instructions, uses fifteen seconds (source/udp.cpp:326). That number
        // is adopted rather than invented, and it is deliberately an order of
        // magnitude clear of the 1500 ms grace whose assertion cost the mic LED
        // and the adaptive triggers on hardware on 2026-08-01.

        [Fact]
        public void AfterFifteenSecondsOfSilence_ThePadIsReleased()
        {
            Assert.True(DualSensePassthroughDispatcher.ShouldReleaseIdleSource(
                driving: true, lastSourcePacketTicks: 1_000, nowTicks: 1_000 + 15_000));
        }

        [Fact]
        public void AGameThatIsMerelyQuiet_KeepsItsTrigger()
        {
            // The case that must not regress. A game can set a trigger at level
            // load and never rewrite it while the player keeps playing.
            Assert.False(DualSensePassthroughDispatcher.ShouldReleaseIdleSource(
                driving: true, lastSourcePacketTicks: 1_000, nowTicks: 1_000 + 14_999));
        }

        [Fact]
        public void ALaneThatNeverDroveThePad_ReleasesNothing()
        {
            // Releasing here would take a trigger this lane never set, which
            // could be the user's own configured one.
            Assert.False(DualSensePassthroughDispatcher.ShouldReleaseIdleSource(
                driving: false, lastSourcePacketTicks: 1_000, nowTicks: 1_000 + 60_000));
        }

        [Fact]
        public void BeforeTheFirstPacket_ThereIsNoSilenceToMeasure()
        {
            Assert.False(DualSensePassthroughDispatcher.ShouldReleaseIdleSource(
                driving: true, lastSourcePacketTicks: 0, nowTicks: 60_000));
        }

        [Fact]
        public void TheReleaseFrame_ClaimsTheTriggersAndNothingElse()
        {
            // The load-bearing guard. PadForge authors the lightbar, the pips,
            // the mic LED and the audio surface on its own Sony pass, and
            // UserEffectsDispatcher is writing them at 30 Hz. A release frame
            // that claimed any of those would fight it. Only valid_flag0 bits 2
            // and 3 may be set, which is what dualsense-tester sets for a
            // trigger update (OutputPanel.vue:230).
            var buffer = new byte[64];               // oversized, as ArrayPool returns
            for (int i = 0; i < buffer.Length; i++)
                buffer[i] = 0xFF;                    // poison, to prove it clears

            DualSensePassthroughDispatcher.BuildTriggerReleasePayload(buffer);

            Assert.Equal(0x0C, buffer[0]);           // right + left trigger valid
            Assert.Equal(0x00, buffer[1]);           // valid_flag1 claims nothing
            Assert.Equal(0x00, buffer[10]);          // right trigger mode = off
            Assert.Equal(0x00, buffer[21]);          // left trigger mode = off

            for (int i = 1; i < 47; i++)
                Assert.Equal(0x00, buffer[i]);       // everything else inert
        }

        [Fact]
        public void TheShutdownFrame_AlsoTakesTheLightbarBack()
        {
            // Measured (#300): the reporter closes PadForge about ten seconds
            // after the game, and the idle reclaim needs fifteen, so the bar
            // was still carrying the game's color on the last line of his
            // trace. The frame we send on the way out has to carry ours.
            var buffer = new byte[64];
            DualSensePassthroughDispatcher.BuildTriggerReleasePayload(buffer);
            DualSensePassthroughDispatcher.AddIdentityLightbar(buffer, 0x00, 0x00, 0x40);

            Assert.Equal(0x0C, buffer[0]);      // triggers still claimed
            Assert.Equal(0x04, buffer[1]);      // lightbar color enable
            Assert.Equal(0x00, buffer[44]);
            Assert.Equal(0x00, buffer[45]);
            Assert.Equal(0x40, buffer[46]);     // player one blue
            Assert.Equal(0x00, buffer[10]);     // and the triggers stay off
            Assert.Equal(0x00, buffer[21]);
        }

        [Fact]
        public void TheShutdownFrame_ClaimsNothingElse()
        {
            // Same load-bearing guard as the plain release: rumble, audio and
            // the pips are other writers' business, and a frame that grabbed
            // them on the way out would fight the Sony pass on its last tick.
            var buffer = new byte[64];
            DualSensePassthroughDispatcher.BuildTriggerReleasePayload(buffer);
            DualSensePassthroughDispatcher.AddIdentityLightbar(buffer, 1, 2, 3);

            for (int i = 2; i < 44; i++)
                Assert.Equal(0x00, buffer[i]);
        }

        [Fact]
        public void WaitMode_ReportsAFullChannel_WhichIsWhatTheFeatureLaneNeeds()
        {
            // Vendor commands still queue, because they are events where order
            // and count matter. They use Wait precisely so a full channel comes
            // back as false and the producer can return the rental.
            var ch = Channel.CreateBounded<int>(new BoundedChannelOptions(2)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            });

            Assert.True(ch.Writer.TryWrite(1));
            Assert.True(ch.Writer.TryWrite(2));
            Assert.False(ch.Writer.TryWrite(3));
        }
    }
}
