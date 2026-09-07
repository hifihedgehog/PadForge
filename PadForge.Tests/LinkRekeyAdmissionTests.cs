using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using PadForge.Engine.RemoteLink;

namespace PadForge.Tests
{
    public sealed class LinkRekeyAdmissionTests
    {
        private sealed class Duplex : ILinkControlChannel
        {
            private readonly Channel<byte[]> _in, _out;
            private Duplex(Channel<byte[]> input, Channel<byte[]> output) { _in = input; _out = output; }
            public Task SendAsync(byte[] message, CancellationToken token) => _out.Writer.WriteAsync(message, token).AsTask();
            public async Task<byte[]> ReceiveAsync(CancellationToken token) => await _in.Reader.ReadAsync(token);
            internal static (Duplex A, Duplex B) Pair()
            {
                var a = Channel.CreateUnbounded<byte[]>();
                var b = Channel.CreateUnbounded<byte[]>();
                return (new Duplex(a, b), new Duplex(b, a));
            }
        }

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public async Task ARequiredRekeyCannotPairWithADifferentPeerOrContinueAfterCancellation(bool cancel)
        {
            var caller = PeerIdentity.Generate();
            var actual = PeerIdentity.Generate();
            var expected = cancel ? actual : PeerIdentity.Generate();
            var trust = new PeerTrustStore();
            trust.Grant(expected.PublicKey, "expected", "t", false, false);
            var exposure = new LinkExposureSnapshot(Array.Empty<RemotePeerDeviceInfo>())
            {
                Reconnect = new LinkReconnectAuthorization(expected.PublicKey, () => !cancel)
            };
            var (a, b) = Duplex.Pair();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            int callerPrompts = 0;
            var responder = LinkConnection.RunResponderAsync(b, actual, new PeerTrustStore(),
                Array.Empty<RemotePeerDeviceInfo>(), Array.Empty<byte>(), _ => true, "t", stop.Token);
            var initiator = LinkConnection.RunInitiatorAsync(a, caller, trust, exposure,
                Array.Empty<byte>(), _ => { callerPrompts++; return true; }, "t", stop.Token);
            try { await Assert.ThrowsAsync<LinkConnectionException>(() => initiator); }
            finally
            {
                stop.Cancel();
                await Record.ExceptionAsync(() => responder);
            }
            Assert.Equal(0, callerPrompts);
            if (!cancel) Assert.False(trust.IsTrusted(actual.PublicKey));
        }

        [Fact]
        public async Task ARequiredRekeyKeepsTheManualTrustChoiceAndUsesFreshDataKeys()
        {
            var caller = PeerIdentity.Generate();
            var peer = PeerIdentity.Generate();
            var callerTrust = new PeerTrustStore();
            var peerTrust = new PeerTrustStore();
            callerTrust.Grant(peer.PublicKey, "peer", "t", false, false);
            peerTrust.Grant(caller.PublicKey, "caller", "t", false, false);
            byte[] previous = null;
            for (int i = 0; i < 2; i++)
            {
                var exposure = new LinkExposureSnapshot(new[] { LinkLifetimeFixtures.Info("source", 230) })
                {
                    Reconnect = new LinkReconnectAuthorization(peer.PublicKey, () => true)
                };
                var (a, b) = Duplex.Pair();
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var responder = LinkConnection.RunResponderAsync(b, peer, peerTrust,
                    Array.Empty<RemotePeerDeviceInfo>(), Array.Empty<byte>(), _ => throw new InvalidOperationException("prompt"), "t", stop.Token);
                var initiator = LinkConnection.RunInitiatorAsync(a, caller, callerTrust, exposure,
                    Array.Empty<byte>(), _ => throw new InvalidOperationException("prompt"), "t", stop.Token);
                var results = await Task.WhenAll(initiator, responder);
                Assert.Equal(results[0].DataKey, results[1].DataKey);
                Assert.Equal((byte)230, Assert.Single(results[1].RemoteDevices).Info.Slot);
                if (previous != null) Assert.False(previous.SequenceEqual(results[0].DataKey));
                previous = results[0].DataKey;
            }
            Assert.False(callerTrust.Find(peer.PublicKey).ReconnectEnabled);
        }

        [Fact]
        public void TheCanonicalDuplicateRuleStillAcceptsAFreshCanonicalReconnect()
        {
            var identity = PeerIdentity.Generate();
            var peer = PeerIdentity.Generate();
            bool canonicalInitiator = identity.Fingerprint.AsSpan().SequenceCompareTo(peer.Fingerprint) < 0;
            foreach (bool reverse in new[] { false, true })
            {
                var trust = new PeerTrustStore();
                trust.Grant(peer.PublicKey, "peer", "t", false, false);
                using var server = new LinkServer(identity, trust, _ => false);
                typeof(LinkServer).GetField("<IsRunning>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(server, true);
                var exposure = new LinkExposureSnapshot(Array.Empty<RemotePeerDeviceInfo>());
                var candidates = new[]
                {
                    Result(peer, !canonicalInitiator, 1),
                    Result(peer, canonicalInitiator, 3),
                    Result(peer, canonicalInitiator, 2)
                };
                foreach (var result in reverse ? candidates.Reverse() : candidates)
                    Register(server, result, exposure);
                var selected = Connection(server);
                var dataSession = (LinkSession)selected.GetType().GetField("DataSession").GetValue(selected);
                var key = (byte[])typeof(LinkSession).GetField("_key", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(dataSession);
                Assert.Equal((byte)(reverse ? 3 : 2), key[0]);
                var lifetime = (LinkConnectionLifetime)selected.GetType().GetField("Lifetime").GetValue(selected);
                var rejected = new LinkExposureSnapshot(Array.Empty<RemotePeerDeviceInfo>())
                {
                    Reconnect = new LinkReconnectAuthorization(peer.PublicKey, () => false)
                };
                Register(server, Result(peer, canonicalInitiator, 0), rejected);
                Assert.Same(selected, Connection(server));
                Assert.True(lifetime.IsCurrent);
            }
        }

        private static LinkConnectionResult Result(PeerIdentity peer, bool initiator, byte key)
            => new()
            {
                PeerFingerprint = peer.Fingerprint, PeerFingerprintHex = peer.FingerprintHex,
                DataKey = Enumerable.Repeat(key, 32).ToArray(), IsInitiator = initiator, PeerPublicKey = peer.PublicKey,
                RemoteDevices = Array.Empty<RemotePeerDevice>()
            };
        private static object Connection(LinkServer server)
            => Assert.Single(((IEnumerable)typeof(LinkServer).GetField("_connections", BindingFlags.NonPublic | BindingFlags.Instance)
                .GetValue(server)).Cast<object>());
        private static void Register(LinkServer server, LinkConnectionResult result, LinkExposureSnapshot exposure)
        {
            var coordinator = (LinkAdmissionCoordinator)typeof(LinkServer).GetField("_admissions", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(server);
            var identity = (PeerIdentity)typeof(LinkServer).GetField("_identity", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(server);
            var admission = coordinator.Capture()(CancellationToken.None);
            admission.BindAsync(result.PeerFingerprintHex, result.IsInitiator,
                result.IsInitiator == (identity.Fingerprint.AsSpan().SequenceCompareTo(result.PeerFingerprint) < 0)).GetAwaiter().GetResult();
            var admitted = new LinkConnectionResult
            {
                DataKey = result.DataKey, IsInitiator = result.IsInitiator, PeerPublicKey = result.PeerPublicKey,
                PeerFingerprint = result.PeerFingerprint, PeerFingerprintHex = result.PeerFingerprintHex,
                RemoteDevices = result.RemoteDevices, Admission = admission
            };
            typeof(LinkServer).GetMethod("Register", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(server, new object[] { admitted, null, null, exposure, null, null, null });
            admission.PeerObserved();
        }
    }
}
