using System.Diagnostics;
using PadForge.Engine;
using PadForge.Engine.RemoteLink;

namespace PadForge.Tests
{
    public class RemotePeerDeviceTests
    {
        private static RemotePeerDeviceInfo GamepadInfo(string peerFp = "aabbccddeeff0011", string devId = "dev0") => new()
        {
            PeerFingerprintHex = peerFp,
            PeerLocalDeviceId = devId,
            Name = "Remote DualSense",
            VendorId = 0x054C,
            ProductId = 0x0CE6,
            NumAxes = 6,
            NumButtons = 17,
            NumHats = 1,
            HasRumble = true,
            InputDeviceType = InputDeviceType.Gamepad,
        };

        private static byte[] EncodeGamepad(System.Action<CustomInputState> mutate)
        {
            var s = CustomInputStateCodec.CreateNeutral();
            mutate(s);
            return CustomInputStateCodec.Encode(s, new CustomInputStateCodec.Caps(false, false));
        }

        [Fact]
        public void ApplyFrame_RoundTripsThroughGetCurrentState()
        {
            var dev = new RemotePeerDevice(GamepadInfo());
            var frame = EncodeGamepad(s => { s.Axis[0] = 1234; s.Buttons[0] = true; s.Buttons[16] = true; s.Povs[0] = 9000; });

            Assert.True(dev.ApplyFramePayload(frame));
            var state = dev.GetCurrentState();

            Assert.NotNull(state);
            Assert.Equal(1234, state.Axis[0]);
            Assert.True(state.Buttons[0]);
            Assert.True(state.Buttons[16]);
            Assert.Equal(9000, state.Povs[0]);
        }

        [Fact]
        public void NewestWins_DropsReorderedOlderFrame()
        {
            var dev = new RemotePeerDevice(GamepadInfo());
            var f1 = EncodeGamepad(s => s.Buttons[0] = true);
            var f2 = EncodeGamepad(s => s.Buttons[1] = true);

            Assert.True(dev.ApplyFramePayload(f2, 200));  // newer
            Assert.True(dev.GetCurrentState().Buttons[1]);

            Assert.False(dev.ApplyFramePayload(f1, 100)); // reordered older -> dropped
            var s = dev.GetCurrentState();
            Assert.True(s.Buttons[1]);                    // still the newer state
            Assert.False(s.Buttons[0]);

            Assert.True(dev.ApplyFramePayload(f1, 300));  // a genuinely newer frame applies
            Assert.True(dev.GetCurrentState().Buttons[0]);
        }

        [Fact]
        public void MalformedFrame_DroppedAndLastGoodHeld()
        {
            var dev = new RemotePeerDevice(GamepadInfo());
            dev.ApplyFramePayload(EncodeGamepad(s => s.Axis[0] = 5000));

            Assert.False(dev.ApplyFramePayload(new byte[] { 0xEE, 0x00 })); // garbage
            var state = dev.GetCurrentState();
            Assert.Equal(5000, state.Axis[0]); // last good retained, not zeroed
        }

        [Fact]
        public void BeforeFirstFrame_ReportsCenteredNotOffline()
        {
            var dev = new RemotePeerDevice(GamepadInfo());
            var state = dev.GetCurrentState();
            Assert.NotNull(state);                 // not offline before any frame
            Assert.Equal(32768, state.Axis[0]);    // centered baseline
        }

        [Fact]
        public void SilentLoss_GoesOfflineAfterStaleWindow()
        {
            long clock = 1000;
            long staleAfterMs = 200;
            var dev = new RemotePeerDevice(GamepadInfo(), (int)staleAfterMs, () => clock);

            dev.ApplyFramePayload(EncodeGamepad(s => s.Buttons[1] = true));
            Assert.NotNull(dev.GetCurrentState()); // fresh frame -> live

            clock += Stopwatch.Frequency * (staleAfterMs + 50) / 1000; // advance past stale window
            Assert.Null(dev.GetCurrentState());    // silent loss -> offline (null)

            dev.ApplyFramePayload(EncodeGamepad(s => s.Buttons[1] = true)); // a frame revives it
            Assert.NotNull(dev.GetCurrentState());
        }

        [Fact]
        public void Identity_IsPeerUnique()
        {
            // Same device id + vid/pid, different peers -> must NOT collide
            // (else the ProductGuid reconnect fallback would alias them).
            var a = new RemotePeerDevice(GamepadInfo(peerFp: "1111111111111111", devId: "dev0"));
            var b = new RemotePeerDevice(GamepadInfo(peerFp: "2222222222222222", devId: "dev0"));

            Assert.NotEqual(a.InstanceGuid, b.InstanceGuid);
            Assert.NotEqual(a.ProductGuid, b.ProductGuid);
        }

        [Fact]
        public void Identity_IsDeterministic()
        {
            var a = new RemotePeerDevice(GamepadInfo());
            var b = new RemotePeerDevice(GamepadInfo());
            Assert.Equal(a.InstanceGuid, b.InstanceGuid);
            Assert.Equal(a.ProductGuid, b.ProductGuid);
            Assert.StartsWith("peer://", a.DevicePath);
        }

        [Fact]
        public void TwoDevicesFromSamePeer_HaveDistinctInstanceGuids()
        {
            var d0 = new RemotePeerDevice(GamepadInfo(devId: "dev0"));
            var d1 = new RemotePeerDevice(GamepadInfo(devId: "dev1"));
            Assert.NotEqual(d0.InstanceGuid, d1.InstanceGuid);
        }

        [Fact]
        public void Rumble_RaisesReturnPathEvent()
        {
            var dev = new RemotePeerDevice(GamepadInfo());
            (ushort low, ushort high)? got = null;
            dev.RumbleRequested += (l, h) => got = (l, h);

            dev.SetRumble(40000, 20000);
            Assert.Equal((ushort)40000, got!.Value.low);
            Assert.Equal((ushort)20000, got!.Value.high);

            dev.StopRumble();
            Assert.Equal((ushort)0, got!.Value.low);
            Assert.Equal((ushort)0, got!.Value.high);
        }

        [Fact]
        public void GetDeviceObjects_SynthesizesGamepadWhenNoneForwarded()
        {
            var dev = new RemotePeerDevice(GamepadInfo());
            var objs = dev.GetDeviceObjects();
            Assert.Equal(6 + 17 + 1, objs.Length); // axes + buttons + pov
        }

        [Fact]
        public void Disposed_GetCurrentStateReturnsNull()
        {
            var dev = new RemotePeerDevice(GamepadInfo());
            dev.Dispose();
            Assert.Null(dev.GetCurrentState());
            Assert.False(dev.IsAttached);
        }
    }
}
