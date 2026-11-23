using System;
using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Engine.RemoteLink;

namespace PadForge.Tests
{
    public partial class LinkServerTests
    {
        /// <summary>A dial on a nonce the always-on responder already answers
        /// waits for that session and starts no second one on its channel: a
        /// second session took the channel's control datagrams from the first,
        /// whose handshake was under way with the peer. Once the answer ends
        /// without a connection the dial registers its own session, after the
        /// answer's cleanup, so that cleanup cannot remove it.</summary>
        [Fact]
        public async Task ADialOnAnAnsweredNonce_WaitsForTheAnswer_ThenRegistersAfterItsCleanup()
        {
            var identity = PeerIdentity.Generate();
            var peer = PeerIdentity.Generate();
            using var server = new LinkServer(identity, new PeerTrustStore(), _ => false);
            int port = StartOnFreePort(server);
            int remotePort;
            do { remotePort = FreePort(); } while (remotePort == port);
            var remote = new IPEndPoint(IPAddress.Loopback, remotePort);
            byte[] nonce = LinkCode.TwoWayPunchNonce(identity.Fingerprint, peer.Fingerprint);
            byte[] probe = new byte[HolePuncher.ProbeLen];
            probe[0] = HolePuncher.TagPing;
            Array.Copy(peer.Fingerprint, 0, probe, 1, HolePuncher.PrefixLen);
            Array.Copy(nonce, 0, probe, 1 + HolePuncher.PrefixLen, HolePuncher.NonceLen);
            const BindingFlags fields = BindingFlags.NonPublic | BindingFlags.Instance;
            var lifetimeField = typeof(LinkServer).GetField("_cts", fields);
            var oldLifetime = (CancellationTokenSource)lifetimeField.GetValue(server);
            var mux = (OwnedLinkCallbacks<uint, Action<byte[]>>)typeof(LinkServer).GetField("_controlSinks", fields).GetValue(server);
            var auto = (ConcurrentDictionary<string, byte>)typeof(LinkServer).GetField("_autoResponding", fields).GetValue(server);
            uint channel = UdpControlChannel.ChannelIdFromNonce(nonce);
            typeof(LinkServer).GetMethod("TryAutoRespondToPunch", fields).Invoke(server, new object[] { remote, probe });
            Assert.True(await WaitUntil(() => mux.TryGetValue(channel, out _), 5000));
            Assert.True(mux.TryGetValue(channel, out var answer));

            // Give the dial a distinct startup cancellation lifetime. The
            // answer keeps its captured token until its real finally runs.
            using var currentLifetime = new CancellationTokenSource();
            using var stopDial = new CancellationTokenSource();
            lifetimeField.SetValue(server, currentLifetime);
            var dial = server.ConnectByPunchAsync(new[] { remote }, nonce,
                LinkCode.IsHandshakeInitiator(identity.Fingerprint, peer.Fingerprint),
                Array.Empty<RemotePeerDeviceInfo>(), TimeSpan.FromSeconds(10), stopDial.Token);
            try
            {
                // The answer still holds the channel alone while it runs.
                await Task.Delay(200);
                Assert.False(dial.IsCompleted);
                Assert.True(mux.TryGetValue(channel, out var during));
                Assert.Same(answer, during);

                // The answer ends with no connection, and the dial then
                // registers a session of its own that the answer's cleanup,
                // already run, leaves in place.
                oldLifetime.Cancel();
                Assert.True(await WaitUntil(() => auto.IsEmpty, 5000));
                Assert.True(await WaitUntil(() => mux.TryGetValue(channel, out var now) && !ReferenceEquals(now, answer), 5000),
                    "the dial never registered its own session after the answer ended");
                Assert.True(mux.TryGetValue(channel, out var current));
                await Task.Delay(200);
                Assert.True(mux.TryGetValue(channel, out var after));
                Assert.Same(current, after);
            }
            finally
            {
                oldLifetime.Cancel();
                stopDial.Cancel();
                await dial.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(await WaitUntil(() => auto.IsEmpty, 5000));
                oldLifetime.Dispose();
            }
            Assert.False(mux.TryGetValue(channel, out _));
        }
    }
}
