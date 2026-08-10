using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PadForge.Engine.RemoteLink.Dht
{
    /// <summary>
    /// Orchestrates DHT presence for the local peer (#294): publishes our
    /// current endpoints on a jittered schedule so a paired peer can always
    /// find us after a move, and looks up a peer's current endpoints on demand.
    /// The scheduling and sequence discipline live here; the wire lives in
    /// <see cref="DhtPresenceStore"/>. Unit-tested against the simulated DHT.
    ///
    /// Sequence: persisted per slot, monotonic. On republish the same content
    /// keeps the sequence (nodes accept an equal seq only for byte-identical
    /// values, so an unchanged republish must NOT bump it); an endpoint change
    /// bumps it. Republish cadence 30-45 min with jitter (BEP 44 items may
    /// expire in ~2h), plus an immediate publish on start and on any endpoint
    /// change. A publish is reported healthy at >= 8 acks.
    /// </summary>
    public sealed class PresenceService : IDisposable
    {
        private static readonly TimeSpan RepublishMin = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan RepublishJitter = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan PresenceTtl = TimeSpan.FromHours(1);

        private readonly IPresenceStore _store;
        private readonly Func<DateTimeOffset> _now;
        private readonly Func<int, int> _jitter; // (maxSeconds) -> seconds, injectable for tests

        public PresenceService(IPresenceStore store, Func<DateTimeOffset> now = null, Func<int, int> jitter = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _now = now ?? (() => DateTimeOffset.UtcNow);
            _jitter = jitter ?? (max => System.Security.Cryptography.RandomNumberGenerator.GetInt32(max + 1));
        }

        /// <summary>One publishing slot: our identity, the pairwise capability
        /// for one peer, our direction, and the monotonic sequence. The sequence
        /// and last-published candidates persist across restarts (the caller
        /// supplies/saves them); an equal candidate set republishes at the same
        /// sequence, a change bumps it.</summary>
        public sealed class Slot
        {
            public byte[] PublisherPublicKey { get; init; }
            public byte[] PublisherPrivateKey { get; init; }
            public byte[] Capability { get; init; }
            public byte Direction { get; init; }
            public long Sequence { get; set; }
            public string LastCandidatesFingerprint { get; set; }
        }

        /// <summary>Publishes the given candidates for a slot. Bumps the slot's
        /// sequence only when the candidate set changed since the last publish,
        /// so an idle republish keeps the same seq (which storage nodes accept
        /// only for identical values). Returns the publish health.</summary>
        public async Task<PublishResult> PublishAsync(
            Slot slot, IReadOnlyList<PresenceRecord.Candidate> candidates, CancellationToken ct)
        {
            string fp = FingerprintCandidates(candidates);
            if (fp != slot.LastCandidatesFingerprint)
            {
                slot.Sequence++;
                slot.LastCandidatesFingerprint = fp;
            }
            var now = _now();
            var presence = new PresenceRecord.Presence
            {
                Candidates = candidates,
                IssuedAt = now,
                Expiry = now + PresenceTtl,
            };
            return await _store.PublishAsync(
                slot.PublisherPublicKey, slot.PublisherPrivateKey,
                slot.Capability, slot.Direction, presence, slot.Sequence, ct).ConfigureAwait(false);
        }

        /// <summary>Looks up a paired peer's current presence, discarding an
        /// expired record (a reader must never act on a stale endpoint).</summary>
        public async Task<PresenceRecord.Presence> LookupAsync(
            byte[] peerPublicKey, byte[] capability, byte peerDirection, CancellationToken ct)
        {
            var pres = await _store.LookupAsync(peerPublicKey, capability, peerDirection, ct).ConfigureAwait(false);
            if (pres == null) return null;
            return pres.IsExpired(_now()) ? null : pres;
        }

        /// <summary>The delay until the next scheduled republish (30-45 min with
        /// jitter). Exposed so the host's timer loop and the tests share one
        /// cadence definition.</summary>
        public TimeSpan NextRepublishDelay()
            => RepublishMin + TimeSpan.FromSeconds(_jitter((int)RepublishJitter.TotalSeconds));

        private static string FingerprintCandidates(IReadOnlyList<PresenceRecord.Candidate> candidates)
        {
            var sb = new System.Text.StringBuilder();
            if (candidates != null)
                foreach (var c in candidates)
                    sb.Append(c.Kind).Append(':').Append(c.Endpoint).Append(';');
            return sb.ToString();
        }

        public void Dispose() { }
    }
}
