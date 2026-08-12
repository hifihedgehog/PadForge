using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace PadForge.Engine.RemoteLink.Dht
{
    /// <summary>
    /// The internet-lane policy loop (#294): for each paired peer that carries a
    /// stored rendezvous capability, publish our current endpoints to the DHT so
    /// the peer can find us after we move, look up the peer's endpoints, and
    /// punch when they are reachable and we are not already connected. Opt-in and
    /// self-contained: constructing it is what turns the lane on, disposing it
    /// turns it off. The mechanisms it drives (STUN, DHT, punch, handshake) are
    /// all separately tested; this is the scheduling glue, and its live residual
    /// is the real DHT plus two real NATs.
    ///
    /// The class is transport-injected (endpoint sources, the presence store, and
    /// the connect/accept actions) so the loop is unit-testable, and so the App
    /// supplies the live LinkServer socket, the STUN result, and the trust store.
    /// </summary>
    public sealed class RemoteLinkInternetService : IDisposable
    {
        /// <summary>One paired peer the loop maintains presence for.</summary>
        public sealed class Peer
        {
            public byte[] PeerPublicKey { get; init; }
            public byte[] Capability { get; init; }
            /// <summary>Our publishing direction (we read the peer's complementary one).</summary>
            public byte SelfDirection { get; init; }
            public byte PeerDirection { get; init; }
            public bool IsConnected { get; set; }
            internal PresenceService.Slot Slot;
        }

        private readonly PresenceService _presence;
        private readonly byte[] _selfPublicKey;
        private readonly byte[] _selfPrivateKey;
        private readonly Func<IReadOnlyList<PresenceRecord.Candidate>> _localCandidates;
        private readonly Func<NatProfile> _localNat;
        // (peerKey, endpoints, nonce, handshakeAsInitiator, ct) -> connected?
        private readonly Func<byte[], IReadOnlyList<IPEndPoint>, byte[], bool, CancellationToken, Task<bool>> _connectByPunch;
        /// <summary>Dial a paired peer at its STABLE identity relay. Needs no
        /// DHT lookup and no direct path, so it is tried first on reconnect.</summary>
        private readonly Func<byte[], byte[], CancellationToken, Task<bool>> _connectByIdentityRelay;
        private readonly byte[] _selfFingerprint;
        private readonly Action<string> _log;

        public RemoteLinkInternetService(
            PresenceService presence, byte[] selfPublicKey, byte[] selfPrivateKey,
            Func<IReadOnlyList<PresenceRecord.Candidate>> localCandidates,
            Func<byte[], IReadOnlyList<IPEndPoint>, byte[], bool, CancellationToken, Task<bool>> connectByPunch,
            Func<NatProfile> localNat = null,
            Action<string> log = null,
            Func<byte[], byte[], CancellationToken, Task<bool>> connectByIdentityRelay = null)
        {
            _presence = presence ?? throw new ArgumentNullException(nameof(presence));
            _selfPublicKey = selfPublicKey;
            _selfPrivateKey = selfPrivateKey;
            _selfFingerprint = selfPublicKey != null ? PeerCrypto.Fingerprint(selfPublicKey) : Array.Empty<byte>();
            _localCandidates = localCandidates ?? (() => Array.Empty<PresenceRecord.Candidate>());
            _localNat = localNat ?? (() => null);
            _connectByPunch = connectByPunch;
            _connectByIdentityRelay = connectByIdentityRelay;
            _log = log ?? (_ => { });
        }

        private PresenceService.Slot SlotFor(Peer p)
            => p.Slot ??= new PresenceService.Slot
            {
                PublisherPublicKey = _selfPublicKey,
                PublisherPrivateKey = _selfPrivateKey,
                Capability = p.Capability,
                Direction = p.SelfDirection,
            };

        /// <summary>Publish our current endpoints for one peer's slot. Called on
        /// startup, on every republish tick, and immediately on an endpoint
        /// change.</summary>
        public Task<PublishResult> PublishAsync(Peer peer, CancellationToken ct)
        {
            var nat = _localNat();
            return _presence.PublishAsync(SlotFor(peer), _localCandidates(),
                nat?.Kind ?? NatKind.Unknown, nat?.Delta ?? 0, ct);
        }

        /// <summary>Look up a peer's current endpoints and, if found and we are
        /// not already connected, punch to them. Returns true if a connection was
        /// established this call. The peer's punch nonce is derived from the
        /// shared capability, so both sides agree with no extra exchange.</summary>
        public async Task<bool> TryReconnectAsync(Peer peer, CancellationToken ct)
        {
            if (peer.IsConnected) return false;

            // RELAY FIRST for reconnect. The punch path below needs BOTH a
            // successful DHT presence lookup and a direct path to exist, and
            // neither is dependable: two machines on different ISPs need not
            // converge on the same DHT records, and behind CGNAT there is no
            // direct path at all. The peer's stable identity relay needs
            // neither, so a paired peer comes back after a restart on any
            // network. Owner report 2026-08-12: close and relaunch never
            // reconnected over the internet, only on a LAN.
            if (_connectByIdentityRelay != null)
            {
                try
                {
                    if (await _connectByIdentityRelay(peer.PeerPublicKey, peer.Capability, ct).ConfigureAwait(false))
                    {
                        peer.IsConnected = true;
                        _log($"reconnect ok via identity relay for {Short(peer.PeerPublicKey)}");
                        return true;
                    }
                }
                catch (Exception ex) { _log($"identity relay reconnect failed: {ex.Message}"); }
            }

            if (_connectByPunch == null) return false;
            var pres = await _presence.LookupAsync(peer.PeerPublicKey, peer.Capability, peer.PeerDirection, ct).ConfigureAwait(false);
            if (pres?.Candidates == null || pres.Candidates.Count == 0) return false;

            var raw = new List<IPEndPoint>(pres.Candidates.Count);
            IPEndPoint peerPublic = null;
            foreach (var c in pres.Candidates)
            {
                raw.Add(c.Endpoint);
                if (c.Kind == PresenceRecord.Candidate.KindPublicV4) peerPublic = c.Endpoint;
            }
            // Predict ports if the peer is behind a sequential-symmetric CGNAT.
            IReadOnlyList<IPEndPoint> endpoints = raw;
            if (pres.NatKind == NatKind.SymmetricSequential && peerPublic != null)
            {
                var peerNat = new NatProfile
                {
                    Kind = NatKind.SymmetricSequential,
                    PublicAddress = peerPublic.Address,
                    LastPort = peerPublic.Port,
                    Delta = pres.NatDelta,
                };
                endpoints = PortPredictor.BuildSprayTargets(peerPublic.Address, peerNat, raw);
            }
            var nonce = PresenceRecord.PunchNonce(peer.Capability);
            // Both peers run this loop and both punch; the lower fingerprint
            // leads the handshake so they never both lead.
            var peerFp = PeerCrypto.Fingerprint(peer.PeerPublicKey);
            bool asInitiator = CompareBytes(_selfFingerprint, peerFp) < 0;

            bool ok = await _connectByPunch(peer.PeerPublicKey, endpoints, nonce, asInitiator, ct).ConfigureAwait(false);
            if (ok) { peer.IsConnected = true; _log($"internet reconnect ok for {Short(peer.PeerPublicKey)}"); }
            return ok;
        }

        /// <summary>Runs one full maintenance pass over all peers: publish
        /// presence for each, then attempt reconnect for the disconnected ones.
        /// The host calls this on the republish cadence and on the discovery
        /// tick.</summary>
        public async Task MaintainAsync(IEnumerable<Peer> peers, CancellationToken ct)
        {
            foreach (var p in peers)
            {
                if (ct.IsCancellationRequested) break;
                try { await PublishAsync(p, ct).ConfigureAwait(false); }
                catch (Exception ex) { _log($"publish failed: {ex.Message}"); }
                try { await TryReconnectAsync(p, ct).ConfigureAwait(false); }
                catch (Exception ex) { _log($"reconnect failed: {ex.Message}"); }
            }
        }

        private static int CompareBytes(byte[] a, byte[] b)
        {
            int n = Math.Min(a.Length, b.Length);
            for (int i = 0; i < n; i++) { int d = a[i] - b[i]; if (d != 0) return d; }
            return a.Length - b.Length;
        }

        private static string Short(byte[] key)
            => key == null ? "?" : Convert.ToHexString(PeerCrypto.Fingerprint(key)).Substring(0, 8);

        public void Dispose() { }
    }
}
