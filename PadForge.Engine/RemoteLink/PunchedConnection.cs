using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace PadForge.Engine.RemoteLink
{
    /// <summary>
    /// Composes the internet-lane connection (#294): hole-punch a path to a
    /// peer, then run the UNMODIFIED authenticated handshake
    /// (<see cref="LinkConnection"/>) over a reliable channel bound to the
    /// punched endpoint. The result is the same <see cref="LinkConnectionResult"/>
    /// the TCP/LAN path produces, so everything downstream (device lists, the
    /// sealed data plane, trust) is identical.
    ///
    /// Transport-abstracted so the whole orchestration is provable in-process
    /// (PunchedConnectionTests drives two peers through a simulated NAT and
    /// confirms the handshake completes with matching data keys). The live
    /// residual is real-NAT punch behavior, nothing in the composition itself.
    ///
    /// The two peers agree on a shared 16-byte punch nonce out of band (from the
    /// self-contained code, or derived from the DHT presence capability). The
    /// nonce authenticates punch probes and scopes the reliable channel id;
    /// real trust is still gated by the handshake's SAS + Ed25519.
    /// </summary>
    public static class PunchedConnection
    {
        /// <summary>Runs the initiator side: punch to one of the peer's
        /// candidates, then handshake as initiator over the won path. Returns
        /// null (no exception) if the punch never lands within
        /// <paramref name="punchTimeout"/>.</summary>
        public static Task<PunchedResult> ConnectInitiatorAsync(
            IPunchTransport punchTransport, IDatagramTransport controlTransport,
            byte[] sharedNonce, IReadOnlyList<IPEndPoint> candidates,
            PeerIdentity identity, PeerTrustStore trust,
            IReadOnlyList<RemotePeerDeviceInfo> exposeLocal, byte[] capabilities,
            Func<PendingPairing, PairingApproval> approve, string nowUtc,
            TimeSpan punchTimeout, CancellationToken ct)
            => RunAsync(true, punchTransport, controlTransport, sharedNonce, candidates,
                identity, trust, exposeLocal, capabilities, approve, nowUtc, punchTimeout, ct);

        /// <summary>Runs the responder side: answer punch probes (candidates may
        /// be empty; the responder learns the initiator's endpoint from the
        /// first valid probe), then handshake as responder over the won
        /// path.</summary>
        public static Task<PunchedResult> ConnectResponderAsync(
            IPunchTransport punchTransport, IDatagramTransport controlTransport,
            byte[] sharedNonce, IReadOnlyList<IPEndPoint> candidates,
            PeerIdentity identity, PeerTrustStore trust,
            IReadOnlyList<RemotePeerDeviceInfo> exposeLocal, byte[] capabilities,
            Func<PendingPairing, PairingApproval> approve, string nowUtc,
            TimeSpan punchTimeout, CancellationToken ct)
            => RunAsync(false, punchTransport, controlTransport, sharedNonce, candidates,
                identity, trust, exposeLocal, capabilities, approve, nowUtc, punchTimeout, ct);

        /// <summary>
        /// The two-way connect (#294 real-NAT fix): BOTH peers spray the other's
        /// candidates AND listen, so both NATs open even when neither is
        /// full-cone (the one-way form, where the host only listened, could not
        /// punch a typical home router). The handshake role is assigned
        /// deterministically by the caller (lower fingerprint = initiator) so the
        /// two sides never both try to lead. Candidates are the peer's endpoints
        /// on both sides.
        /// </summary>
        public static Task<PunchedResult> ConnectTwoWayAsync(
            IPunchTransport punchTransport, IDatagramTransport controlTransport,
            byte[] sharedNonce, IReadOnlyList<IPEndPoint> candidates, bool handshakeAsInitiator,
            PeerIdentity identity, PeerTrustStore trust,
            IReadOnlyList<RemotePeerDeviceInfo> exposeLocal, byte[] capabilities,
            Func<PendingPairing, PairingApproval> approve, string nowUtc,
            TimeSpan punchTimeout, CancellationToken ct,
            IEnumerable<IPEndPoint> selfEndpoints = null)
            => RunAsync(handshakeAsInitiator, punchTransport, controlTransport, sharedNonce, candidates,
                identity, trust, exposeLocal, capabilities, approve, nowUtc, punchTimeout, ct, selfEndpoints);

        /// <summary>The relay-lane composition (#294): the same UNMODIFIED
        /// handshake as the punched path, minus the punch, because the
        /// transport is already a working path through an iroh relay. Used
        /// when no direct path can be punched (both peers behind CGNAT or
        /// symmetric NAT). Returns null on timeout/cancel like the punched
        /// forms so the caller surfaces a clean failure.</summary>
        public static async Task<LinkConnectionResult> ConnectRelayAsync(
            IDatagramTransport controlTransport, byte[] sharedNonce, bool isInitiator,
            PeerIdentity identity, PeerTrustStore trust,
            IReadOnlyList<RemotePeerDeviceInfo> exposeLocal, byte[] capabilities,
            Func<PendingPairing, PairingApproval> approve, string nowUtc,
            CancellationToken ct)
        {
            uint channelId = UdpControlChannel.ChannelIdFromNonce(sharedNonce);
            using var channel = new UdpControlChannel(controlTransport, channelId: channelId);
            try
            {
                return isInitiator
                    ? await LinkConnection.RunInitiatorAsync(channel, identity, trust, exposeLocal, capabilities, approve, nowUtc, ct).ConfigureAwait(false)
                    : await LinkConnection.RunResponderAsync(channel, identity, trust, exposeLocal, capabilities, approve, nowUtc, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return null; }
        }

        /// <summary>Relay handshake on an EXPLICIT channel id (#294 code lane).
        /// The code-derived lane fixes the channel from the code itself, so a
        /// listening host can demux a call before it knows the caller.</summary>
        public static async Task<LinkConnectionResult> ConnectRelayOnChannelAsync(
            IDatagramTransport controlTransport, uint channelId, bool isInitiator,
            PeerIdentity identity, PeerTrustStore trust,
            IReadOnlyList<RemotePeerDeviceInfo> exposeLocal, byte[] capabilities,
            Func<PendingPairing, PairingApproval> approve, string nowUtc,
            CancellationToken ct)
        {
            using var channel = new UdpControlChannel(controlTransport, channelId: channelId);
            try
            {
                return isInitiator
                    ? await LinkConnection.RunInitiatorAsync(channel, identity, trust, exposeLocal, capabilities, approve, nowUtc, ct).ConfigureAwait(false)
                    : await LinkConnection.RunResponderAsync(channel, identity, trust, exposeLocal, capabilities, approve, nowUtc, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return null; }
        }

        private static async Task<PunchedResult> RunAsync(
            bool isInitiator, IPunchTransport punchTransport, IDatagramTransport controlTransport,
            byte[] sharedNonce, IReadOnlyList<IPEndPoint> candidates,
            PeerIdentity identity, PeerTrustStore trust,
            IReadOnlyList<RemotePeerDeviceInfo> exposeLocal, byte[] capabilities,
            Func<PendingPairing, PairingApproval> approve, string nowUtc,
            TimeSpan punchTimeout, CancellationToken ct,
            IEnumerable<IPEndPoint> selfEndpoints = null)
        {
            // Probes carry OUR fingerprint prefix so an unsolicited peer can
            // derive the shared nonce and auto-answer without clicking Connect.
            var puncher = new HolePuncher(punchTransport, sharedNonce, sprayInterval: null,
                selfEndpoints: selfEndpoints, selfFingerprint: identity?.Fingerprint);
            using var punchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            punchCts.CancelAfter(punchTimeout);

            IPEndPoint won;
            try { won = await puncher.PunchAsync(candidates ?? Array.Empty<IPEndPoint>(), punchCts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { won = null; }
            if (won == null) return null; // punch failed: caller falls back to Connect by Address

            // Reliable channel over the won path, scoped by the shared nonce.
            uint channelId = UdpControlChannel.ChannelIdFromNonce(sharedNonce);
            using var channel = new UdpControlChannel(controlTransport, channelId: channelId);

            var result = isInitiator
                ? await LinkConnection.RunInitiatorAsync(channel, identity, trust, exposeLocal, capabilities, approve, nowUtc, ct).ConfigureAwait(false)
                : await LinkConnection.RunResponderAsync(channel, identity, trust, exposeLocal, capabilities, approve, nowUtc, ct).ConfigureAwait(false);

            return new PunchedResult { Connection = result, PeerEndpoint = won };
        }
    }

    /// <summary>The handshake result plus the punched UDP endpoint the data
    /// plane sends to.</summary>
    public sealed class PunchedResult
    {
        public LinkConnectionResult Connection { get; init; }
        public IPEndPoint PeerEndpoint { get; init; }
    }
}
