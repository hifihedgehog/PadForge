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

        private static async Task<PunchedResult> RunAsync(
            bool isInitiator, IPunchTransport punchTransport, IDatagramTransport controlTransport,
            byte[] sharedNonce, IReadOnlyList<IPEndPoint> candidates,
            PeerIdentity identity, PeerTrustStore trust,
            IReadOnlyList<RemotePeerDeviceInfo> exposeLocal, byte[] capabilities,
            Func<PendingPairing, PairingApproval> approve, string nowUtc,
            TimeSpan punchTimeout, CancellationToken ct)
        {
            var puncher = new HolePuncher(punchTransport, sharedNonce);
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
