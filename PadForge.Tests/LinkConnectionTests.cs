using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using PadForge.Engine;
using PadForge.Engine.RemoteLink;

namespace PadForge.Tests
{
    public class LinkConnectionTests
    {
        // Buffered in-memory duplex — what A sends, B receives, and vice versa.
        private sealed class MemChannel : ILinkControlChannel
        {
            private readonly Channel<byte[]> _out, _in;
            private MemChannel(Channel<byte[]> outbound, Channel<byte[]> inbound) { _out = outbound; _in = inbound; }
            public Task SendAsync(byte[] m, CancellationToken ct) => _out.Writer.WriteAsync(m, ct).AsTask();
            public async Task<byte[]> ReceiveAsync(CancellationToken ct) => await _in.Reader.ReadAsync(ct);
            public static (MemChannel a, MemChannel b) Pair()
            {
                var ab = Channel.CreateUnbounded<byte[]>();
                var ba = Channel.CreateUnbounded<byte[]>();
                return (new MemChannel(ab, ba), new MemChannel(ba, ab));
            }
        }

        private static RemotePeerDeviceInfo PadInfo() => new()
        {
            PeerLocalDeviceId = "pad0", Name = "A Pad",
            VendorId = 0x054C, ProductId = 0x0CE6,
            NumAxes = 6, NumButtons = 17, NumHats = 1,
            HasRumble = true, InputDeviceType = InputDeviceType.Gamepad,
        };

        private static readonly byte[] Caps = { 1, 0 };

        [Fact]
        public async Task Pairing_EstablishesExchangesDevicesAndKeysCarryInput()
        {
            var (chA, chB) = MemChannel.Pair();
            var idA = PeerIdentity.Generate();
            var idB = PeerIdentity.Generate();
            var trustA = new PeerTrustStore();
            var trustB = new PeerTrustStore();
            Func<PendingPairing, bool> approve = _ => true;

            // A responder exposes a pad; B initiator consumes.
            var taskA = LinkConnection.RunResponderAsync(chA, idA, trustA, new[] { PadInfo() }, Caps, approve, "2026-06-13T00:00:00Z");
            var taskB = LinkConnection.RunInitiatorAsync(chB, idB, trustB, Array.Empty<RemotePeerDeviceInfo>(), Caps, approve, "2026-06-13T00:00:00Z");
            var rA = await taskA;
            var rB = await taskB;

            // B received A's exposed device, salted by A's authenticated identity.
            Assert.Single(rB.RemoteDevices);
            Assert.Equal("A Pad", rB.RemoteDevices[0].Name);
            Assert.StartsWith("peer://", rB.RemoteDevices[0].DevicePath);
            Assert.Empty(rA.RemoteDevices); // B exposed nothing

            // Both sides pinned each other (first contact -> explicit grant).
            Assert.True(trustA.IsTrusted(idB.PublicKey));
            Assert.True(trustB.IsTrusted(idA.PublicKey));

            // The negotiated data keys actually open a LinkSession: A streams, B receives.
            var sA = new LinkSession(rA.DataKey, rA.IsInitiator);
            var sB = new LinkSession(rB.DataKey, rB.IsInitiator);
            var state = CustomInputStateCodec.CreateNeutral();
            state.Buttons[0] = true;
            var dg = sA.Seal(LinkMessageType.Input, 0, 1, CustomInputStateCodec.Encode(state, new CustomInputStateCodec.Caps(false, false)));
            Assert.True(sB.Open(dg, out _, out _, out _, out var payload));

            rB.RemoteDevices[0].ApplyFramePayload(payload);
            Assert.True(rB.RemoteDevices[0].GetCurrentState().Buttons[0]);
        }

        [Fact]
        public async Task FirstContactRejected_AbortsBothSides()
        {
            var (chA, chB) = MemChannel.Pair();
            Func<PendingPairing, bool> reject = _ => false;

            var taskA = LinkConnection.RunResponderAsync(chA, PeerIdentity.Generate(), new PeerTrustStore(), new[] { PadInfo() }, Caps, reject, "t");
            var taskB = LinkConnection.RunInitiatorAsync(chB, PeerIdentity.Generate(), new PeerTrustStore(), Array.Empty<RemotePeerDeviceInfo>(), Caps, reject, "t");

            await Assert.ThrowsAsync<LinkConnectionException>(() => taskA);
            await Assert.ThrowsAsync<LinkConnectionException>(() => taskB);
        }

        [Fact]
        public async Task KnownPeer_Reconnects_WithoutPrompting()
        {
            var (chA, chB) = MemChannel.Pair();
            var idA = PeerIdentity.Generate();
            var idB = PeerIdentity.Generate();
            // Pre-pin each other (a prior pairing), auto-select on.
            var trustA = new PeerTrustStore(new[] { PeerTrust.FromPublicKey(idB.PublicKey, "B", "t", true, false) });
            var trustB = new PeerTrustStore(new[] { PeerTrust.FromPublicKey(idA.PublicKey, "A", "t", true, false) });
            Func<PendingPairing, bool> mustNotPrompt = _ => throw new Exception("reconnect must not prompt");

            var taskA = LinkConnection.RunResponderAsync(chA, idA, trustA, new[] { PadInfo() }, Caps, mustNotPrompt, "t");
            var taskB = LinkConnection.RunInitiatorAsync(chB, idB, trustB, Array.Empty<RemotePeerDeviceInfo>(), Caps, mustNotPrompt, "t");
            await taskA;
            var rB = await taskB;

            Assert.Single(rB.RemoteDevices); // established with no SAS prompt
        }

        [Fact]
        public async Task PeerFingerprint_IsTheAuthenticatedPeerIdentity()
        {
            var (chA, chB) = MemChannel.Pair();
            var idA = PeerIdentity.Generate();
            var idB = PeerIdentity.Generate();
            Func<PendingPairing, bool> approve = _ => true;

            var taskA = LinkConnection.RunResponderAsync(chA, idA, new PeerTrustStore(), Array.Empty<RemotePeerDeviceInfo>(), Caps, approve, "t");
            var taskB = LinkConnection.RunInitiatorAsync(chB, idB, new PeerTrustStore(), Array.Empty<RemotePeerDeviceInfo>(), Caps, approve, "t");
            var rA = await taskA;
            var rB = await taskB;

            Assert.Equal(idB.FingerprintHex, rA.PeerFingerprintHex); // A sees B
            Assert.Equal(idA.FingerprintHex, rB.PeerFingerprintHex); // B sees A
        }
    }
}
