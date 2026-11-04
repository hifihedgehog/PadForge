using System;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The POWERPLAY mat's own lighting (#557): its LIGHTSPEED receiver,
    /// 046D:C53A, carries the mat as device index 7, past the six slots the
    /// pairing table lists (libratbag data/devices/logitech-g-powerplay.device:3-8,
    /// Solaar lib/logitech_receiver/receiver.py:352-361). The walk asks
    /// index 7 on that receiver alone, as a slot the table never reads
    /// empty.
    /// </summary>
    public class HidppPowerPlayTests
    {
        /// <summary>A LIGHTSPEED receiver whose pairing table lists a mouse
        /// in slot 1 and nothing else, and whose devices answer as
        /// <paramref name="devices"/> does.</summary>
        private static HidppHapticsTests.FakeChannel Receiver(Func<byte, byte, byte, byte[], HidppReply> devices)
            => new()
            {
                Answer = devices,
                Register = (r, s) => r == 0xB5 && s == 0x20
                    ? new HidppReply(HidppReplyKind.Answer)
                    : new HidppReply(HidppReplyKind.Error, 0x03),
            };

        private static Func<byte, byte, byte, byte[], HidppReply> MouseAndMat()
        {
            var mouse = HidppHapticsTests.Mouse(1);
            var mat = HidppHapticsTests.Mouse(7, "G POWERPLAY", type: 6);
            return (d, f, fn, p) => d == 7 ? mat(d, f, fn, p) : mouse(d, f, fn, p);
        }

        [Fact]
        public void OnlyThePowerPlayReceiver_CarriesADeviceOfItsOwn()
        {
            Assert.True(HidppReceiverProtocol.HasBuiltInDevice(0xC53A));
            Assert.False(HidppReceiverProtocol.HasBuiltInDevice(0xC539));
            Assert.False(HidppReceiverProtocol.HasBuiltInDevice(0xC548));
            Assert.Equal(HidppReceiverKind.Unifying, HidppReceiverProtocol.KindOf(0xC53A));
            Assert.Equal(7, HidppHapticProtocol.BuiltInDeviceIndex);
            Assert.Equal(8, new HidppPathState().Settled.Length);
        }

        [Fact]
        public void TheWalkReachesIndex7_OnThePowerPlayReceiver()
        {
            var channel = Receiver(MouseAndMat());
            var state = new HidppPathState { ReceiverKind = HidppReceiverKind.Unifying, BuiltInDevice = true };

            var found = HidppUnitProbe.Probe(channel, state, Guid.Empty, 0);

            Assert.Equal(new byte[] { 1, 7 }, found.Select(u => u.DeviceIndex).OrderBy(i => i));
            Assert.Equal("G POWERPLAY", found.Single(u => u.DeviceIndex == 7).Name);
            Assert.True(state.Settled[7]);
            // The table is read for the six slots alone: 0x20 to 0x25.
            Assert.DoesNotContain(channel.Traffic, t => t.Kind == "reg" && t.Frame.Length > 4 && t.Frame[4] == 0x26);
            // Empty slots 2 to 6 are never asked.
            Assert.All(channel.Asked, a => Assert.True(a.Device == 1 || a.Device == 7, $"asked 0x{a.Device:X2}"));
        }

        [Fact]
        public void AnotherReceiver_IsNeverAskedForIndex7()
        {
            var channel = Receiver(MouseAndMat());
            var state = new HidppPathState { ReceiverKind = HidppReceiverKind.Unifying };

            var found = HidppUnitProbe.Probe(channel, state, Guid.Empty, 0);

            Assert.Equal(new byte[] { 1 }, found.Select(u => u.DeviceIndex));
            Assert.DoesNotContain(channel.Asked, a => a.Device == 7);
            Assert.False(HidppUnitProbe.HasOpenSlot(state, bluetooth: false));
        }

        /// <summary>A mat that does not answer keeps its index open and
        /// waiting, backs off as a slot does, and is found once it
        /// answers.</summary>
        [Fact]
        public void ASilentMat_StaysOpen_AndIsFoundWhenItAnswers()
        {
            bool matUp = false;
            var both = MouseAndMat();
            var channel = Receiver((d, f, fn, p) => d == 7 && !matUp ? HidppHapticsTests.NoAnswer() : both(d, f, fn, p));
            var state = new HidppPathState { ReceiverKind = HidppReceiverKind.Unifying, BuiltInDevice = true };

            Assert.Equal(new byte[] { 1 }, HidppUnitProbe.Probe(channel, state, Guid.Empty, 0).Select(u => u.DeviceIndex));
            Assert.True(state.Open(7));
            Assert.True(HidppUnitProbe.Pending(state, bluetooth: false));
            Assert.True(HidppUnitProbe.HasOpenSlot(state, bluetooth: false));
            Assert.Equal(HidppHapticProbe.FirstRetryMs, state.RetryAt[7]);

            matUp = true;
            var found = HidppUnitProbe.Probe(channel, state, Guid.Empty, HidppHapticProbe.FirstRetryMs);
            Assert.Equal(7, Assert.Single(found).DeviceIndex);
            Assert.False(HidppUnitProbe.HasOpenSlot(state, bluetooth: false));
        }

        [Fact]
        public void TheLastSlot_FollowsThePath()
        {
            Assert.Equal(6, HidppUnitProbe.LastSlot(new HidppPathState(), bluetooth: false));
            Assert.Equal(7, HidppUnitProbe.LastSlot(new HidppPathState { BuiltInDevice = true }, bluetooth: false));
            Assert.Equal(0, HidppUnitProbe.LastSlot(new HidppPathState { BuiltInDevice = true }, bluetooth: true));
            Assert.Equal(0, HidppUnitProbe.LastSlot(new HidppPathState { BuiltInDevice = true, Direct = true }, bluetooth: false));
        }
    }
}
