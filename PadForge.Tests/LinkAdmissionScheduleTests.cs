using System;
using System.Collections;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using PadForge.Engine.RemoteLink;

namespace PadForge.Tests
{
    [Collection("RemoteLinkSockets")]
    public sealed class LinkAdmissionScheduleTests
    {
        private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
        private sealed class Duplex : ILinkControlChannel
        {
            private readonly Channel<byte[]> _input, _output;
            private Duplex(Channel<byte[]> input, Channel<byte[]> output) { _input = input; _output = output; }
            public Task SendAsync(byte[] message, CancellationToken token) => _output.Writer.WriteAsync(message, token).AsTask();
            public async Task<byte[]> ReceiveAsync(CancellationToken token) => await _input.Reader.ReadAsync(token);
            internal static (Duplex A, Duplex B) Pair()
            {
                var a = Channel.CreateUnbounded<byte[]>();
                var b = Channel.CreateUnbounded<byte[]>();
                return (new Duplex(a, b), new Duplex(b, a));
            }
        }

        private static LinkServer Server(PeerIdentity identity, PeerTrustStore trust)
        {
            var server = new LinkServer(identity, trust, _ => false);
            typeof(LinkServer).GetField("<IsRunning>k__BackingField", Private).SetValue(server, true);
            typeof(LinkServer).GetField("_cts", Private).SetValue(server, new CancellationTokenSource());
            return server;
        }
        private static LinkExposureSnapshot Exposure(LinkServer server, params RemotePeerDeviceInfo[] devices)
            => (LinkExposureSnapshot)typeof(LinkServer).GetMethod("PrepareExposure", Private)
                .Invoke(server, new object[] { new LinkDeviceInventory(1, devices) });
        private static bool Register(LinkServer server, LinkConnectionResult result, LinkExposureSnapshot exposure, TcpClient tcp = null)
            => (bool)typeof(LinkServer).GetMethod("Register", Private)
                .Invoke(server, new object[] { result, tcp, null, exposure, null, null, null });
        private static object Connection(LinkServer server)
            => Assert.Single(((IEnumerable)typeof(LinkServer).GetField("_connections", Private).GetValue(server)).Cast<object>());
        private static T Field<T>(object connection, string name)
            => (T)connection.GetType().GetField(name).GetValue(connection);
        private static byte[] Key(LinkServer server)
            => (byte[])typeof(LinkSession).GetField("_key", Private).GetValue(Field<LinkSession>(Connection(server), "DataSession"));
        private delegate void Route(ReadOnlySpan<byte> bytes, IPEndPoint from);
        private static void Deliver(LinkServer server, byte[] packet, IPEndPoint from = null)
            => typeof(LinkServer).GetMethod("RouteDatagram", Private).CreateDelegate<Route>(server)(packet, from);
        private static (PeerIdentity A, PeerIdentity B) Identities()
        {
            var a = PeerIdentity.Generate();
            var b = PeerIdentity.Generate();
            return a.Fingerprint.AsSpan().SequenceCompareTo(b.Fingerprint) < 0 ? (a, b) : (b, a);
        }

        [Fact]
        public async Task ANewCanonicalAdmissionCannotPassAPausedOlderRegistration()
        {
            var (identityA, identityB) = Identities();
            var trustA = new PeerTrustStore();
            var trustB = new PeerTrustStore();
            trustA.Grant(identityB.PublicKey, "B", "t", false, false);
            trustB.Grant(identityA.PublicKey, "A", "t", false, false);
            using var a = Server(identityA, trustA);
            using var b = Server(identityB, trustB);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var exposeAX = Exposure(a);
            var exposeBX = Exposure(b);
            var (ax, bx) = Duplex.Pair();
            var bxTask = LinkConnection.RunResponderAsync(bx, identityB, trustB, exposeBX, Array.Empty<byte>(), _ => false, "t", stop.Token);
            var axTask = LinkConnection.RunInitiatorAsync(ax, identityA, trustA, exposeAX, Array.Empty<byte>(), _ => false, "t", stop.Token);
            var x = await Task.WhenAll(axTask, bxTask);
            Assert.True(Register(a, x[0], exposeAX));
            // B's X handshake is complete, but its handler has not registered X.
            // Start required recovery on A with no guessed route in this controlled fixture.
            typeof(LinkServer).GetMethod("QueueRekey", Private).Invoke(a, new[] { Connection(a) });
            var recovery = Assert.Single(a.RekeyTasks);
            using (var stopY = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
            {
                var (ay, by) = Duplex.Pair();
                var byTask = LinkConnection.RunResponderAsync(by, identityB, trustB, Exposure(b), Array.Empty<byte>(), _ => false, "t", stopY.Token);
                var ayTask = LinkConnection.RunInitiatorAsync(ay, identityA, trustA, Exposure(a), Array.Empty<byte>(), _ => false, "t", stopY.Token);
                try
                {
                    await Assert.ThrowsAsync<LinkConnectionException>(() => byTask.WaitAsync(TimeSpan.FromSeconds(5)));
                    Assert.False(recovery.IsCompleted);
                }
                finally
                {
                    stopY.Cancel();
                    await Record.ExceptionAsync(() => ayTask);
                }
            }
            Assert.True(Register(b, x[1], exposeBX));
            byte[] lateX = Field<LinkSession>(Connection(b), "DataSession").Seal(LinkMessageType.Keepalive, 0, 0, Array.Empty<byte>());

            // A fresh canonical manual attempt is accepted inside the original collision window.
            var exposeAY = Exposure(a);
            var exposeBY = Exposure(b);
            var (ayFresh, byFresh) = Duplex.Pair();
            var byFreshTask = LinkConnection.RunResponderAsync(byFresh, identityB, trustB, exposeBY, Array.Empty<byte>(), _ => false, "t", stop.Token);
            var ayFreshTask = LinkConnection.RunInitiatorAsync(ayFresh, identityA, trustA, exposeAY, Array.Empty<byte>(), _ => false, "t", stop.Token);
            var y = await Task.WhenAll(ayFreshTask, byFreshTask);
            Assert.True(Register(a, y[0], exposeAY));
            Assert.True(Register(b, y[1], exposeBY));
            Assert.Equal(y[0].DataKey, Key(a));
            Assert.Equal(Key(a), Key(b));
            Deliver(a, lateX);
            Assert.False(Field<TaskCompletionSource>(Connection(a), "Confirmed").Task.IsCompleted);
            Deliver(a, Field<LinkSession>(Connection(b), "DataSession").Seal(LinkMessageType.Keepalive, 0, 0, Array.Empty<byte>()));
            await recovery.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(Key(a), Key(b));
            Assert.False(trustA.Find(identityB.PublicKey).ReconnectEnabled);
        }

        [Fact]
        public async Task RevokedPassiveResultStaysRejectedAfterAFreshManualPairing()
        {
            var (identityA, identityB) = Identities();
            var trustA = new PeerTrustStore();
            var trustB = new PeerTrustStore();
            trustA.Grant(identityB.PublicKey, "B", "t", false, false);
            trustB.Grant(identityA.PublicKey, "A", "t", false, false);
            using var a = Server(identityA, trustA);
            using var b = Server(identityB, trustB);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var oldA = Exposure(a);
            var oldB = Exposure(b);
            var (ax, bx) = Duplex.Pair();
            var bxTask = LinkConnection.RunResponderAsync(bx, identityB, trustB, oldB, Array.Empty<byte>(), _ => false, "t", stop.Token);
            var axTask = LinkConnection.RunInitiatorAsync(ax, identityA, trustA, oldA, Array.Empty<byte>(), _ => false, "t", stop.Token);
            var old = await Task.WhenAll(axTask, bxTask);
            Assert.True(Register(a, old[0], oldA));
            Assert.True(trustB.Revoke(identityA.PublicKey));
            b.RevokePeer(identityA.FingerprintHex);
            Assert.False(Register(b, old[1], oldB));
            Assert.False(b.HasConnections);
            a.RevokePeer(identityB.FingerprintHex);

            int prompts = 0;
            var freshA = Exposure(a);
            var freshB = Exposure(b);
            var (ay, by) = Duplex.Pair();
            var byTask = LinkConnection.RunResponderAsync(by, identityB, trustB, freshB, Array.Empty<byte>(),
                _ => { prompts++; return true; }, "t", stop.Token);
            var ayTask = LinkConnection.RunInitiatorAsync(ay, identityA, trustA, freshA, Array.Empty<byte>(), _ => false, "t", stop.Token);
            var fresh = await Task.WhenAll(ayTask, byTask);
            Assert.True(Register(a, fresh[0], freshA));
            Assert.True(Register(b, fresh[1], freshB));
            Assert.Equal(1, prompts);
            Assert.True(trustB.IsTrusted(identityA.PublicKey));
            var current = Connection(b);
            Assert.False(Register(b, old[1], oldB));
            Assert.Same(current, Connection(b));
            Assert.Equal(Key(a), Key(b));
        }

        [Fact]
        public async Task PeerTrafficDuringPublicationReleasesTheInitiatorLeaseAfterTheCommit()
        {
            var coordinator = new LinkAdmissionCoordinator();
            using var first = coordinator.Capture()(CancellationToken.None);
            await first.BindAsync("peer", true, true);
            Task observed = null;
            using var entered = new ManualResetEventSlim();
            Assert.True(first.TryPublish(() =>
            {
                observed = Task.Run(() => { entered.Set(); first.PeerObserved(); });
                Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
                Assert.False(observed.Wait(100));
                return true;
            }));
            await observed.WaitAsync(TimeSpan.FromSeconds(5));
            using var next = coordinator.Capture()(CancellationToken.None);
            await next.BindAsync("peer", true, true).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(next.TryPublish(() => true));
            next.PeerObserved();
        }

        [Fact]
        public async Task RevocationDuringApprovalCannotRestoreTheRemovedGrant()
        {
            var (identityA, identityB) = Identities();
            var trustA = new PeerTrustStore();
            var trustB = new PeerTrustStore();
            using var a = Server(identityA, trustA);
            using var b = Server(identityB, trustB);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var (channelA, channelB) = Duplex.Pair();
            int prompts = 0;
            var passive = LinkConnection.RunResponderAsync(channelB, identityB, trustB, Exposure(b), Array.Empty<byte>(),
                _ => { prompts++; b.RevokePeer(identityA.FingerprintHex); return true; }, "t", stop.Token);
            var active = LinkConnection.RunInitiatorAsync(channelA, identityA, trustA, Exposure(a), Array.Empty<byte>(),
                _ => true, "t", stop.Token);
            try { await Assert.ThrowsAsync<LinkConnectionException>(() => passive); }
            finally { stop.Cancel(); await Record.ExceptionAsync(() => active); }
            Assert.Equal(1, prompts);
            Assert.False(trustB.IsTrusted(identityA.PublicKey));
            Assert.False(b.HasConnections);
        }

        [Fact]
        public async Task AResponderRetainsOnlyRouteLearningUntilAuthenticatedUdpSuppliesTheEndpoint()
        {
            var identity = PeerIdentity.Generate();
            var peer = PeerIdentity.Generate();
            var trust = new PeerTrustStore();
            trust.Grant(peer.PublicKey, "peer", "t", false, false);
            using var server = Server(identity, trust);
            var exposure = Exposure(server, LinkLifetimeFixtures.Info("a"));
            var admission = exposure.AdmissionFactory(CancellationToken.None);
            await admission.BindAsync(peer.FingerprintHex, false,
                identity.Fingerprint.AsSpan().SequenceCompareTo(peer.Fingerprint) > 0);
            var key = PeerIdentity.Generate().PublicKey;
            var result = new LinkConnectionResult
            {
                DataKey = key, IsInitiator = false, PeerPublicKey = peer.PublicKey,
                PeerFingerprint = peer.Fingerprint, PeerFingerprintHex = peer.FingerprintHex,
                Admission = admission, RemoteDevices = Array.Empty<RemotePeerDevice>()
            };
            using var tcp = new TcpClient();
            Assert.True(Register(server, result, exposure, tcp));
            var connection = Connection(server);
            var lifetime = Field<LinkConnectionLifetime>(connection, "Lifetime");
            int effects = 0;
            server.FrameReceived += _ => effects++;
            typeof(LinkServer).GetMethod("QueueRekey", Private).Invoke(server, new[] { connection });
            var recovery = Assert.Single(server.RekeyTasks);
            Assert.False(server.HasConnections);
            Assert.False(lifetime.IsCurrent);
            Assert.Null(Field<IPEndPoint>(connection, "PeerUdpEndpoint"));
            Assert.Null(Field<IPEndPoint>(connection, "KnownTcpTarget"));
            Assert.Same(tcp, Field<TcpClient>(connection, "Tcp"));
            var learned = Field<TaskCompletionSource<IPEndPoint>>(connection, "EndpointLearned");
            Assert.False(learned.Task.IsCompleted);
            var observed = new IPEndPoint(IPAddress.Loopback, 54321);
            var wrong = new LinkSession(PeerIdentity.Generate().PublicKey, true);
            Deliver(server, wrong.Seal(LinkMessageType.Keepalive, 0, 0, Array.Empty<byte>()), observed);
            Assert.False(learned.Task.IsCompleted);
            var sender = new LinkSession(key, true);
            Deliver(server, sender.Seal(LinkMessageType.Output, 0, 1, LinkLifetimeFixtures.Effect(2, true)), observed);
            Assert.Equal(observed, await learned.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(0, effects);
            Assert.False(lifetime.TryCommit(lifetime.CaptureLocalBinding(0), 1, 2, () => effects++));
            server.RevokePeer(peer.FingerprintHex);
            await recovery.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(Field<TcpClient>(connection, "Tcp"));
            Assert.False(server.HasConnections);
        }
    }
}
