using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace PadForge.Engine.RemoteLink
{
    /// <summary>Endpoint-addressed datagram I/O for the punch (#294 step 4).
    /// The real implementation is LinkServer's bound UDP socket; the tests use
    /// an in-process simulated NAT (two endpoints with a port-rewriting middle).
    /// Abstracting it keeps the punch orchestration deterministically testable
    /// without real NATs, which is the named live residual.</summary>
    public interface IPunchTransport
    {
        Task SendToAsync(byte[] datagram, IPEndPoint destination, CancellationToken ct);
        /// <summary>Owner routes inbound punch datagrams here (source endpoint +
        /// bytes). Set by the puncher.</summary>
        Action<IPEndPoint, byte[]> OnDatagram { get; set; }
    }

    /// <summary>
    /// UDP hole punching orchestration (#294 step 4): both peers spray small
    /// authenticated probes at each candidate endpoint pair simultaneously so
    /// each NAT sees an outgoing packet and opens a mapping. The first candidate
    /// that answers is the working path.
    ///
    /// Candidate order is private-first (two machines that share a LAN just
    /// connect, no NAT), then IPv6-direct when present (no NAT to punch), then
    /// the public STUN-learned endpoint. A shared 16-byte punch nonce (from the
    /// rendezvous or the long code) tags every probe so a stray datagram can
    /// never be mistaken for a peer's probe. The punch proves reachability only;
    /// the existing SAS + Ed25519 handshake (LinkConnection) then runs over the
    /// won endpoint via <see cref="UdpControlChannel"/> and gates real trust.
    /// </summary>
    public sealed class HolePuncher
    {
        private const byte TagPing = 0xC2;
        private const byte TagPong = 0xC3;
        private const int NonceLen = 16;

        private readonly IPunchTransport _transport;
        private readonly byte[] _nonce;
        private readonly TimeSpan _sprayInterval;

        private readonly object _lock = new();
        private IPEndPoint _won;
        private TaskCompletionSource<IPEndPoint> _winTcs;

        public HolePuncher(IPunchTransport transport, byte[] sharedNonce, TimeSpan? sprayInterval = null)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            if (sharedNonce == null || sharedNonce.Length != NonceLen)
                throw new ArgumentException($"Punch nonce must be {NonceLen} bytes.", nameof(sharedNonce));
            _nonce = (byte[])sharedNonce.Clone();
            _sprayInterval = sprayInterval ?? TimeSpan.FromMilliseconds(200);
            _transport.OnDatagram = OnDatagram;
        }

        /// <summary>Orders raw candidate endpoints into punch priority:
        /// private (LAN) first, IPv6 next, public IPv4 last. De-dups.</summary>
        public static IReadOnlyList<IPEndPoint> OrderCandidates(
            IPEndPoint privateEndpoint, IPEndPoint publicEndpoint, IPEndPoint ipv6Endpoint = null)
        {
            var list = new List<IPEndPoint>();
            void Add(IPEndPoint ep)
            {
                if (ep == null) return;
                foreach (var e in list) if (e.Equals(ep)) return;
                list.Add(ep);
            }
            Add(privateEndpoint);
            Add(ipv6Endpoint);
            Add(publicEndpoint);
            return list;
        }

        /// <summary>
        /// Sprays probes at every candidate until one answers or the token
        /// cancels, returning the working endpoint (or null on timeout). Both
        /// peers call this at the same time after the READY exchange. Receiving
        /// a peer's ping OR a pong both prove the path, so a single round trip
        /// in either direction settles it.
        /// </summary>
        public async Task<IPEndPoint> PunchAsync(
            IReadOnlyList<IPEndPoint> candidates, CancellationToken ct)
        {
            // An empty candidate list is the RESPONDER mode: it sprays nothing
            // but still listens, settling on the first valid probe's source
            // (the initiator's learned endpoint). Only a null list is a no-op.
            if (candidates == null) return null;

            var winTcs = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_lock)
            {
                if (_won != null) return _won;
                _winTcs = winTcs;
            }

            var ping = new byte[1 + NonceLen];
            ping[0] = TagPing;
            _nonce.CopyTo(ping, 1);

            using var reg = ct.Register(() => winTcs.TrySetResult(null));
            var sprayer = Task.Run(async () =>
            {
                try
                {
                    while (!ct.IsCancellationRequested && !winTcs.Task.IsCompleted)
                    {
                        foreach (var ep in candidates)
                        {
                            try { await _transport.SendToAsync(ping, ep, ct).ConfigureAwait(false); }
                            catch { /* one bad candidate never aborts the spray */ }
                        }
                        try { await Task.Delay(_sprayInterval, ct).ConfigureAwait(false); }
                        catch (OperationCanceledException) { break; }
                    }
                }
                catch (OperationCanceledException) { }
            }, ct);

            var winner = await winTcs.Task.ConfigureAwait(false);
            try { await sprayer.ConfigureAwait(false); } catch { }
            return winner;
        }

        private void OnDatagram(IPEndPoint from, byte[] dg)
        {
            if (dg == null || dg.Length != 1 + NonceLen) return;
            byte tag = dg[0];
            if (tag != TagPing && tag != TagPong) return;
            // Constant-time nonce compare so a probe with the wrong nonce (a
            // stray or a different pairing racing on the same socket) is
            // rejected before it can settle the punch.
            if (!CryptographicOperations.FixedTimeEquals(dg.AsSpan(1, NonceLen), _nonce)) return;

            if (tag == TagPing)
            {
                // Answer every valid ping with a pong to its source, so the peer
                // learns the path from its side too. Fire-and-forget.
                var pong = new byte[1 + NonceLen];
                pong[0] = TagPong;
                _nonce.CopyTo(pong, 1);
                _ = _transport.SendToAsync(pong, from, CancellationToken.None);
            }

            // Either a ping or a pong from a valid peer proves this endpoint is
            // reachable. First one wins.
            lock (_lock)
            {
                if (_won != null) return;
                _won = from;
                _winTcs?.TrySetResult(from);
            }
        }

        /// <summary>The settled endpoint once the punch has won, else null.</summary>
        public IPEndPoint WonEndpoint { get { lock (_lock) return _won; } }
    }
}
