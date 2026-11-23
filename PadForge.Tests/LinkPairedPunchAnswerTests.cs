using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Engine.RemoteLink;
using PadForge.Engine.RemoteLink.Dht;

namespace PadForge.Tests
{
    public partial class LinkServerTests
    {
        private sealed class NoPresenceStore : IPresenceStore
        {
            public Task<PublishResult> PublishAsync(byte[] a, byte[] b, byte[] c, byte d, PresenceRecord.Presence e, long f, CancellationToken ct)
                => Task.FromResult(new PublishResult { AckCount = 0 });
            public Task<PresenceRecord.Presence> LookupAsync(byte[] a, byte[] b, byte c, CancellationToken ct)
                => Task.FromResult<PresenceRecord.Presence>(null);
        }

        private static byte[] PunchProbe(byte[] senderFingerprint, byte[] nonce)
        {
            var probe = new byte[HolePuncher.ProbeLen];
            probe[0] = HolePuncher.TagPing;
            Array.Copy(senderFingerprint, 0, probe, 1, HolePuncher.PrefixLen);
            Array.Copy(nonce, 0, probe, 1 + HolePuncher.PrefixLen, HolePuncher.NonceLen);
            return probe;
        }

        // A paired peer's internet reconnect punches with the capability nonce
        // both sides derive. The idle host answers that dial only once its
        // maintenance pass has told the listener which peers are paired, and
        // answers in the role the dialing pass expects.
        [Fact]
        public async Task PairedPeerCapabilityDial_IsAnsweredAfterAMaintenancePass()
        {
            var identity = PeerIdentity.Generate();
            var peer = PeerIdentity.Generate();
            var cap = new byte[32];
            new Random(5).NextBytes(cap);
            using var server = new LinkServer(identity, new PeerTrustStore(), _ => false);
            int port = StartOnFreePort(server);
            int remotePort;
            do { remotePort = FreePort(); } while (remotePort == port);
            var remote = new IPEndPoint(IPAddress.Loopback, remotePort);

            var svc = new RemoteLinkInternetService(
                new PresenceService(new NoPresenceStore()), identity.PublicKey, identity.ExportPrivateKey(),
                localCandidates: () => new List<PresenceRecord.Candidate>(),
                connectByPunch: (k, e, n, i, ct) => Task.FromResult(false),
                answerPunchFrom: server.SetKnownPunchPeers);

            byte[] nonce = PresenceRecord.PunchNonce(cap);
            byte[] probe = PunchProbe(peer.Fingerprint, nonce);
            const BindingFlags fields = BindingFlags.NonPublic | BindingFlags.Instance;
            var auto = (ConcurrentDictionary<string, byte>)typeof(LinkServer).GetField("_autoResponding", fields).GetValue(server);
            var respond = typeof(LinkServer).GetMethod("TryAutoRespondToPunch", fields);

            // Before any pass the dial is none of ours.
            Assert.Null(server.AutoRespondRole(peer.Fingerprint[..HolePuncher.PrefixLen], nonce));
            respond.Invoke(server, new object[] { remote, probe });
            Assert.True(auto.IsEmpty);

            // The pass marks the peer connected so it dials nothing itself.
            var p = new RemoteLinkInternetService.Peer
            {
                PeerPublicKey = peer.PublicKey, Capability = cap,
                SelfDirection = PresenceRecord.DirectionA, PeerDirection = PresenceRecord.DirectionB,
                IsConnected = true,
            };
            await svc.MaintainAsync(new[] { p }, CancellationToken.None);

            // The peer's pass leads when its fingerprint is the lower one, so
            // the host answers in the other role.
            bool peerLeads = RemoteLinkInternetService.LeadsHandshake(peer.Fingerprint, identity.Fingerprint);
            Assert.Equal(!peerLeads, server.AutoRespondRole(peer.Fingerprint[..HolePuncher.PrefixLen], nonce));

            // This host's own probe coming back through a hairpinning router
            // carries the same nonce and its own prefix: still none of ours.
            Assert.Null(server.AutoRespondRole(identity.Fingerprint[..HolePuncher.PrefixLen], nonce));

            respond.Invoke(server, new object[] { remote, probe });
            Assert.False(auto.IsEmpty);

            // A pass that no longer lists the peer takes the answer back.
            await svc.MaintainAsync(Array.Empty<RemoteLinkInternetService.Peer>(), CancellationToken.None);
            Assert.Null(server.AutoRespondRole(peer.Fingerprint[..HolePuncher.PrefixLen], nonce));
        }
    }
}
