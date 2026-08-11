using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Engine;
using PadForge.Engine.RemoteLink;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// #294 end-to-end composition: hole punch, then the UNMODIFIED handshake
    /// over the reliable-UDP channel bound to the punched path, yielding the
    /// same LinkConnectionResult the TCP/LAN path produces. Driven through a
    /// simulated NAT that carries BOTH punch and control datagrams on one link
    /// and demuxes them by tag, exactly as LinkServer's real receive loop does.
    /// This proves the orchestration; real-NAT punch behavior is the residual.
    /// </summary>
    public class PunchedConnectionTests
    {
        private static readonly byte[] Caps = { 1, 0 };

        private static RemotePeerDeviceInfo PadInfo() => new()
        {
            PeerLocalDeviceId = "pad0", Name = "Punch Pad",
            VendorId = 0x054C, ProductId = 0x0CE6,
            NumAxes = 6, NumButtons = 17, NumHats = 1,
            HasRumble = true, InputDeviceType = InputDeviceType.Gamepad,
        };

        [Fact]
        public async Task Punch_ThenHandshake_EstablishesWithMatchingDataKeys()
        {
            var fabric = new SimPunchFabric();
            var epA = new IPEndPoint(IPAddress.Parse("203.0.113.1"), 1111);
            var epB = new IPEndPoint(IPAddress.Parse("203.0.113.2"), 2222);
            var a = fabric.Endpoint(epA);
            var b = fabric.Endpoint(epB);

            var idA = PeerIdentity.Generate();
            var idB = PeerIdentity.Generate();
            var trustA = new PeerTrustStore();
            var trustB = new PeerTrustStore();
            var nonce = new byte[16]; for (int i = 0; i < 16; i++) nonce[i] = (byte)(i + 1);
            Func<PendingPairing, PairingApproval> approve = _ => true;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            // A initiates (knows B's endpoint), B responds (empty candidates,
            // learns A from the first probe), same as the real code/DHT lane.
            var taskA = PunchedConnection.ConnectInitiatorAsync(
                a.Punch, a.Control, nonce, new[] { epB },
                idA, trustA, Array.Empty<RemotePeerDeviceInfo>(), Caps, approve, "t",
                TimeSpan.FromSeconds(5), cts.Token);
            var taskB = PunchedConnection.ConnectResponderAsync(
                b.Punch, b.Control, nonce, Array.Empty<IPEndPoint>(),
                idB, trustB, new[] { PadInfo() }, Caps, approve, "t",
                TimeSpan.FromSeconds(5), cts.Token);

            var rA = await taskA;
            var rB = await taskB;

            Assert.NotNull(rA);
            Assert.NotNull(rB);
            Assert.Equal(epB, rA.PeerEndpoint);
            Assert.Equal(epA, rB.PeerEndpoint);

            // Both pinned each other, and the negotiated data keys interoperate.
            Assert.True(trustA.IsTrusted(idB.PublicKey));
            Assert.True(trustB.IsTrusted(idA.PublicKey));
            Assert.Single(rA.Connection.RemoteDevices); // B exposed a pad
            // Peer devices carry the owning machine's name so a consumer can
            // tell them from local ones (regression fixed 2026-08-11).
            Assert.StartsWith("Punch Pad", rA.Connection.RemoteDevices[0].Name);
            Assert.Contains($"({LinkConnection.SafeMachineName()})", rA.Connection.RemoteDevices[0].Name);

            var sA = new LinkSession(rA.Connection.DataKey, rA.Connection.IsInitiator);
            var sB = new LinkSession(rB.Connection.DataKey, rB.Connection.IsInitiator);
            var state = CustomInputStateCodec.CreateNeutral();
            state.Buttons[0] = true;
            var dg = sB.Seal(LinkMessageType.Input, 0, 1, CustomInputStateCodec.Encode(state, new CustomInputStateCodec.Caps(false, false)));
            Assert.True(sA.Open(dg, out _, out _, out _, out var payload));
            rA.Connection.RemoteDevices[0].ApplyFramePayload(payload);
            Assert.True(rA.Connection.RemoteDevices[0].GetCurrentState().Buttons[0]);

            // Both derived the same DHT rendezvous capability through the punched
            // handshake (the reconnect-after-move path depends on it).
            Assert.Equal(trustA.GetRendezvousCapability(idB.PublicKey),
                         trustB.GetRendezvousCapability(idA.PublicKey));
        }

        [Fact]
        public async Task TwoWay_BothSpray_RestrictedNat_ConnectsWithRoles()
        {
            // The real-NAT fix: BOTH sides spray the other's candidates (a
            // one-way punch where only one side fired could not open a
            // restricted-cone NAT). The handshake role is assigned by
            // fingerprint so exactly one side leads. This drives both sides
            // through ConnectTwoWayAsync with each other's endpoints.
            var fabric = new SimPunchFabric();
            var epA = new IPEndPoint(IPAddress.Parse("203.0.113.1"), 1111);
            var epB = new IPEndPoint(IPAddress.Parse("203.0.113.2"), 2222);
            var a = fabric.Endpoint(epA);
            var b = fabric.Endpoint(epB);
            var idA = PeerIdentity.Generate();
            var idB = PeerIdentity.Generate();
            var nonce = new byte[16]; for (int i = 0; i < 16; i++) nonce[i] = (byte)(i + 3);
            Func<PendingPairing, PairingApproval> approve = _ => true;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            // Role by fingerprint: lower leads. Both compute it consistently.
            bool aLeads = Compare(idA.Fingerprint, idB.Fingerprint) < 0;

            var taskA = PunchedConnection.ConnectTwoWayAsync(
                a.Punch, a.Control, nonce, new[] { epB }, aLeads,
                idA, new PeerTrustStore(), new[] { PadInfo() }, Caps, approve, "t",
                TimeSpan.FromSeconds(6), cts.Token);
            var taskB = PunchedConnection.ConnectTwoWayAsync(
                b.Punch, b.Control, nonce, new[] { epA }, !aLeads,
                idB, new PeerTrustStore(), new[] { PadInfo() }, Caps, approve, "t",
                TimeSpan.FromSeconds(6), cts.Token);

            var rA = await taskA;
            var rB = await taskB;
            Assert.NotNull(rA);
            Assert.NotNull(rB);
            Assert.Equal(epB, rA.PeerEndpoint);
            Assert.Equal(epA, rB.PeerEndpoint);
            // Data keys interoperate both ways.
            var sA = new LinkSession(rA.Connection.DataKey, rA.Connection.IsInitiator);
            var sB = new LinkSession(rB.Connection.DataKey, rB.Connection.IsInitiator);
            var st = CustomInputStateCodec.CreateNeutral(); st.Buttons[1] = true;
            var dg = sA.Seal(LinkMessageType.Input, 0, 1, CustomInputStateCodec.Encode(st, new CustomInputStateCodec.Caps(false, false)));
            Assert.True(sB.Open(dg, out _, out _, out _, out _));
        }

        private static int Compare(byte[] a, byte[] b)
        {
            for (int i = 0; i < Math.Min(a.Length, b.Length); i++) { int d = a[i] - b[i]; if (d != 0) return d; }
            return a.Length - b.Length;
        }

        [Fact]
        public async Task Punch_TimesOut_WhenPeerNeverAnswers()
        {
            var fabric = new SimPunchFabric();
            var a = fabric.Endpoint(new IPEndPoint(IPAddress.Parse("203.0.113.1"), 1111));
            var dead = new IPEndPoint(IPAddress.Parse("203.0.113.9"), 9999); // nobody bound
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            var r = await PunchedConnection.ConnectInitiatorAsync(
                a.Punch, a.Control, new byte[16], new[] { dead },
                PeerIdentity.Generate(), new PeerTrustStore(), Array.Empty<RemotePeerDeviceInfo>(),
                Caps, _ => true, "t", TimeSpan.FromMilliseconds(300), cts.Token);
            Assert.Null(r); // punch failed -> caller falls back to Connect by Address
        }
    }
}
