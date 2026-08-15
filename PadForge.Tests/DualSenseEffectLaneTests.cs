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

        // ── A repeat yields to a pending payload, and nothing more ──
        //
        // Measured (#300), both halves. A burst is around 19,000 packets a
        // second repeating this lane's last write, carrying roughly 90 real
        // changes among them.
        //
        // Filtering repeats at the SAMPLER lost every change: the spam
        // overwrote a change microseconds after it landed and the next sample
        // saw only a repeat. The trace read writes of ZERO for seconds.
        //
        // Dropping repeats OUTRIGHT broke the opposite way, because this lane
        // is not the pad's only writer: UserEffectsDispatcher writes the same
        // report to the same device at 30 Hz while mirroring a subsystem the
        // game drives. A repeat re-asserts the game's payload over that pass,
        // so suppressing repeats left the game winning only as often as it
        // changed state, 6 times a second inside a burst against a writer
        // running at 30.
        //
        // Hence the rule under test: drop a repeat only while something is
        // already waiting.

        [Fact]
        public void ARepeatYieldsToAPendingPayload_SoASpamBurstCannotEvictAChange()
        {
            var lastSent = new byte[] { 0x02, 0x11, 0x22, 0x33 };
            var repeat = new byte[] { 0x02, 0x11, 0x22, 0x33 };
            Assert.True(DualSensePassthroughDispatcher.ShouldDropAtDoor(
                repeat, lastSent, lastSent.Length, somethingPending: true));
        }

        [Fact]
        public void ARepeatWithNothingWaiting_IsKept_BecauseItReassertsAgainstTheOtherWriter()
        {
            // The regression this pins. Dropping this packet is what let the
            // 30 Hz pass hold the pad between the game's real changes.
            var lastSent = new byte[] { 0x02, 0x11, 0x22, 0x33 };
            var repeat = new byte[] { 0x02, 0x11, 0x22, 0x33 };
            Assert.False(DualSensePassthroughDispatcher.ShouldDropAtDoor(
                repeat, lastSent, lastSent.Length, somethingPending: false));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AGenuineChange_IsNeverDroppedAtTheDoor(bool somethingPending)
        {
            var lastSent = new byte[] { 0x02, 0x11, 0x22, 0x33 };
            var changed = new byte[] { 0x02, 0x11, 0x22, 0x34 };
            Assert.False(DualSensePassthroughDispatcher.ShouldDropAtDoor(
                changed, lastSent, lastSent.Length, somethingPending));
        }

        [Fact]
        public void ARepeatOfWhatThePadHolds_IsRecognised()
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
