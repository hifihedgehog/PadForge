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
        private readonly Func<byte[], IReadOnlyList<IPEndPoint>, byte[], CancellationToken, Task<bool>> _connectByPunch;
        private readonly Action<string> _log;

        public RemoteLinkInternetService(
            PresenceService presence, byte[] selfPublicKey, byte[] selfPrivateKey,
            Func<IReadOnlyList<PresenceRecord.Candidate>> localCandidates,
            Func<byte[], IReadOnlyList<IPEndPoint>, byte[], CancellationToken, Task<bool>> connectByPunch,
            Action<string> log = null)
        {
            _presence = presence ?? throw new ArgumentNullException(nameof(presence));
            _selfPublicKey = selfPublicKey;
            _selfPrivateKey = selfPrivateKey;
            _localCandidates = localCandidates ?? (() => Array.Empty<PresenceRecord.Candidate>());
            _connectByPunch = connectByPunch;
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
            => _presence.PublishAsync(SlotFor(peer), _localCandidates(), ct);

        /// <summary>Look up a peer's current endpoints and, if found and we are
        /// not already connected, punch to them. Returns true if a connection was
        /// established this call. The peer's punch nonce is derived from the
        /// shared capability, so both sides agree with no extra exchange.</summary>
        public async Task<bool> TryReconnectAsync(Peer peer, CancellationToken ct)
        {
            if (peer.IsConnected || _connectByPunch == null) return false;
            var pres = await _presence.LookupAsync(peer.PeerPublicKey, peer.Capability, peer.PeerDirection, ct).ConfigureAwait(false);
            if (pres?.Candidates == null || pres.Candidates.Count == 0) return false;

            var endpoints = new List<IPEndPoint>(pres.Candidates.Count);
            foreach (var c in pres.Candidates) endpoints.Add(c.Endpoint);
            var nonce = PresenceRecord.PunchNonce(peer.Capability);

            bool ok = await _connectByPunch(peer.PeerPublicKey, endpoints, nonce, ct).ConfigureAwait(false);
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

        private static string Short(byte[] key)
            => key == null ? "?" : Convert.ToHexString(PeerCrypto.Fingerprint(key)).Substring(0, 8);

        public void Dispose() { }
    }
}
