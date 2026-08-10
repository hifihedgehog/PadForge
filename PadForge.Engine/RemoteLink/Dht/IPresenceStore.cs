using System;
using System.Threading;
using System.Threading.Tasks;

namespace PadForge.Engine.RemoteLink.Dht
{
    /// <summary>Outcome of a presence publication.</summary>
    public sealed class PublishResult
    {
        /// <summary>Nodes that acknowledged the put. The presence lane treats
        /// >= 8 as healthy (BEP 44's own "8 closest" backoff condition).</summary>
        public int AckCount { get; init; }
        public bool Healthy => AckCount >= 8;
    }

    /// <summary>
    /// The presence-store seam (#294): how a paired peer's CURRENT endpoints are
    /// found after either side moves, with zero PadForge infrastructure. The DHT
    /// implementation is the primary; the seam exists so the code lane, and any
    /// future operator-less substrate, plug in behind one interface without
    /// touching the connect orchestration.
    /// </summary>
    public interface IPresenceStore
    {
        /// <summary>Publish our current signed+encrypted presence for a peer to
        /// look up. Idempotent per (capability, direction) slot: a later publish
        /// with a higher sequence replaces the earlier one.</summary>
        Task<PublishResult> PublishAsync(
            byte[] publisherPublicKey, byte[] publisherPrivateKey,
            byte[] capability, byte direction,
            PresenceRecord.Presence presence, long seq, CancellationToken ct);

        /// <summary>Look up a paired peer's current presence by the pinned
        /// capability + the peer's public key + the peer's publishing direction.
        /// Returns null when nothing valid/current is found (peer absent OR the
        /// substrate is unhealthy; the caller distinguishes via the store's own
        /// health signal).</summary>
        Task<PresenceRecord.Presence> LookupAsync(
            byte[] peerPublicKey, byte[] capability, byte peerDirection, CancellationToken ct);
    }
}
