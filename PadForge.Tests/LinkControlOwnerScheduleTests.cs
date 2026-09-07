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
        [Fact]
        public async Task OldAutoResponderCleanupCannotRemoveANewerManualPunchRegistration()
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
            Assert.True(mux.TryGetValue(channel, out var previous));

            // Give the replacement attempt a distinct startup cancellation lifetime.
            // The old automatic path keeps its captured token until its real finally runs.
            using var currentLifetime = new CancellationTokenSource();
            using var stopManual = new CancellationTokenSource();
            lifetimeField.SetValue(server, currentLifetime);
            var manual = server.ConnectByPunchAsync(new[] { remote }, nonce,
                LinkCode.IsHandshakeInitiator(identity.Fingerprint, peer.Fingerprint),
                Array.Empty<RemotePeerDeviceInfo>(), TimeSpan.FromSeconds(10), stopManual.Token);
            try
            {
                Assert.True(mux.TryGetValue(channel, out var current));
                Assert.NotSame(previous, current);
                oldLifetime.Cancel();
                Assert.True(await WaitUntil(() => auto.IsEmpty, 5000));
                Assert.True(mux.TryGetValue(channel, out var after));
                Assert.Same(current, after);
            }
            finally
            {
                oldLifetime.Cancel();
                stopManual.Cancel();
                await manual.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(await WaitUntil(() => auto.IsEmpty, 5000));
                oldLifetime.Dispose();
            }
            Assert.False(mux.TryGetValue(channel, out _));
        }
    }
}
