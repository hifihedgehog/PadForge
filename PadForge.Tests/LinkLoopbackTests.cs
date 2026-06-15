using System;
using System.Buffers.Binary;
using PadForge.Engine;
using PadForge.Engine.RemoteLink;

namespace PadForge.Tests
{
    /// <summary>
    /// End-to-end composition of the whole Remote Link foundation in-process:
    /// pairing handshake -> keyed LinkSession -> CustomInputState codec ->
    /// RemotePeerDevice, with the haptic return path. This is the spec's
    /// "loopback test, no hardware required" at the library level — it proves the
    /// six increments work as one system before any socket is involved.
    /// </summary>
    public class LinkLoopbackTests
    {
        private static (HandshakeResult init, HandshakeResult resp) Handshake()
        {
            var i = new LinkHandshake(PeerIdentity.Generate(), new byte[] { 1, 0 }, isInitiator: true);
            var r = new LinkHandshake(PeerIdentity.Generate(), new byte[] { 1, 0 }, isInitiator: false);
            byte[] commit = i.StartCommit();
            byte[] revealR = r.OnInitiatorCommit(commit);
            byte[] revealI = i.OnResponderReveal(revealR);
            byte[] confirm = r.OnInitiatorReveal(revealI);
            i.OnResponderConfirm(confirm);
            return (i.Result, r.Result);
        }

        [Fact]
        public void InputFlows_SenderDeviceToReceiverRemotePeerDevice()
        {
            var (init, resp) = Handshake();
            var aSession = new LinkSession(init.SessionKey, isInitiator: true);   // device holder
            var bSession = new LinkSession(resp.SessionKey, isInitiator: false);  // game host

            // B builds a RemotePeerDevice for A's gamepad from the DEVICE_ADD descriptor.
            var bDevice = new RemotePeerDevice(new RemotePeerDeviceInfo
            {
                PeerFingerprintHex = Convert.ToHexString(resp.PeerFingerprint), // = A's fingerprint
                PeerLocalDeviceId = "pad0",
                Name = "Remote Pad",
                NumAxes = 6,
                NumButtons = 17,
                NumHats = 1,
                HasRumble = true,
                InputDeviceType = InputDeviceType.Gamepad,
            });

            // A's local device moves: left stick + A button + d-pad east.
            var aState = CustomInputStateCodec.CreateNeutral();
            aState.Axis[0] = 1000;
            aState.Buttons[0] = true;
            aState.Povs[0] = 9000;
            byte[] payload = CustomInputStateCodec.Encode(aState, new CustomInputStateCodec.Caps(false, false));
            byte[] datagram = aSession.Seal(LinkMessageType.Input, slotId: 0, timestampUs: 12345, payload);

            // B receives, opens, applies.
            Assert.True(bSession.Open(datagram, out var type, out _, out var ts, out var got));
            Assert.Equal(LinkMessageType.Input, type);
            Assert.Equal(12345UL, ts);
            Assert.True(bDevice.ApplyFramePayload(got));

            var bState = bDevice.GetCurrentState();
            Assert.NotNull(bState);
            Assert.Equal(1000, bState.Axis[0]);
            Assert.True(bState.Buttons[0]);
            Assert.Equal(9000, bState.Povs[0]);
        }

        [Fact]
        public void Streaming_NeutralFrameReleasesHeldInput()
        {
            var (init, resp) = Handshake();
            var a = new LinkSession(init.SessionKey, isInitiator: true);
            var b = new LinkSession(resp.SessionKey, isInitiator: false);
            var bDevice = new RemotePeerDevice(new RemotePeerDeviceInfo
            {
                PeerFingerprintHex = Convert.ToHexString(resp.PeerFingerprint),
                PeerLocalDeviceId = "pad0", NumAxes = 6, NumButtons = 17, NumHats = 1,
                InputDeviceType = InputDeviceType.Gamepad,
            });
            var caps = new CustomInputStateCodec.Caps(false, false);

            var pressed = CustomInputStateCodec.CreateNeutral();
            pressed.Buttons[5] = true;
            b.Open(a.Seal(LinkMessageType.Input, 0, 1, CustomInputStateCodec.Encode(pressed, caps)), out _, out _, out _, out var p1);
            bDevice.ApplyFramePayload(p1);
            Assert.True(bDevice.GetCurrentState().Buttons[5]);

            // The terminal "released" frame must clear it — absolute newest-wins.
            var released = CustomInputStateCodec.CreateNeutral();
            b.Open(a.Seal(LinkMessageType.Input, 0, 2, CustomInputStateCodec.Encode(released, caps)), out _, out _, out _, out var p2);
            bDevice.ApplyFramePayload(p2);
            Assert.False(bDevice.GetCurrentState().Buttons[5]);
        }

        [Fact]
        public void HapticFlows_HostBackToDeviceHolder()
        {
            var (init, resp) = Handshake();
            var aSession = new LinkSession(init.SessionKey, isInitiator: true);   // device holder receives haptics
            var bSession = new LinkSession(resp.SessionKey, isInitiator: false);  // host produces haptics

            var bDevice = new RemotePeerDevice(new RemotePeerDeviceInfo
            {
                PeerFingerprintHex = Convert.ToHexString(resp.PeerFingerprint),
                PeerLocalDeviceId = "pad0", NumAxes = 6, NumButtons = 17, NumHats = 1,
                HasRumble = true, InputDeviceType = InputDeviceType.Gamepad,
            });

            // The host engine drives rumble on the remote device; the transport seals
            // a Haptic datagram from the RumbleRequested event and sends it to A.
            byte[] hapticDatagram = null;
            bDevice.RumbleRequested += (low, high) =>
            {
                var pl = new byte[4];
                BinaryPrimitives.WriteUInt16LittleEndian(pl.AsSpan(0), low);
                BinaryPrimitives.WriteUInt16LittleEndian(pl.AsSpan(2), high);
                hapticDatagram = bSession.Seal(LinkMessageType.Haptic, 0, 999, pl);
            };

            bDevice.SetRumble(40000, 20000);
            Assert.NotNull(hapticDatagram);

            // A (device holder) opens the haptic and would replay it on the physical pad.
            Assert.True(aSession.Open(hapticDatagram, out var type, out _, out _, out var pl));
            Assert.Equal(LinkMessageType.Haptic, type);
            Assert.Equal((ushort)40000, BinaryPrimitives.ReadUInt16LittleEndian(pl.AsSpan(0)));
            Assert.Equal((ushort)20000, BinaryPrimitives.ReadUInt16LittleEndian(pl.AsSpan(2)));
        }

        [Fact]
        public void CrossSessionDatagram_Rejected()
        {
            // A datagram sealed under one paired session must not open under a
            // different pairing's keys (fresh ephemerals => unrelated session keys).
            var (init1, _) = Handshake();
            var (_, resp2) = Handshake();
            var a1 = new LinkSession(init1.SessionKey, isInitiator: true);
            var bOther = new LinkSession(resp2.SessionKey, isInitiator: false);

            var dg = a1.Seal(LinkMessageType.Input, 0, 1,
                CustomInputStateCodec.Encode(CustomInputStateCodec.CreateNeutral(), new CustomInputStateCodec.Caps(false, false)));
            Assert.False(bOther.Open(dg, out _, out _, out _, out _));
        }
    }
}
