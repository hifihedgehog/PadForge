using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Parameters;

namespace PadForge.Engine.RemoteLink
{
    /// <summary>
    /// Client for the iroh relay protocol (#294), the guaranteed lane when a
    /// hole punch cannot land.
    ///
    /// WHY. Hole punching cannot succeed on every network pair: where both
    /// sides sit behind carrier-grade or symmetric NAT there is no direct
    /// path, full stop. Every product that "just works" therefore carries a
    /// relay fallback. The reference implementation this was studied from
    /// (FlexInput, crates/net/src/transport/p2p.rs) gets exactly this from
    /// iroh: its N0 preset supplies free public relays run by n0.computer and
    /// falls back to them automatically. PadForge hosts nothing, so it speaks
    /// the same open protocol to the same free relays.
    ///
    /// PROTOCOL, cite-verified against the cloned iroh repo (iroh-relay/src):
    ///  - WebSocket to wss://host/relay, subprotocol "iroh-relay-v2"
    ///    (http.rs: RELAY_PATH, ProtocolVersion::V2).
    ///  - Server sends ServerChallenge (tag 0 + 16B challenge). Client replies
    ///    ClientAuth (tag 1 + 32B Ed25519 public key + serde_bytes signature:
    ///    varint length 0x40 + 64B). The signed message is
    ///    blake3::derive_key("iroh-relay handshake v1 challenge signature",
    ///    challenge) (protos/handshake.rs). Server answers
    ///    ServerConfirmsAuth (tag 2) or ServerDeniesAuth (tag 3).
    ///  - Datagrams: send tag 4 + 32B destination key + ECN byte + payload;
    ///    receive tag 6 + 32B source key + ECN byte + payload
    ///    (protos/relay.rs Frame::write_to, protos/common.rs FrameType).
    ///  - Server pings (tag 9 + 8B payload); client echoes Pong (tag 10).
    /// Handshake + datagram delivery were both verified LIVE against
    /// use1-1.relay.n0.iroh.link before this class shipped.
    ///
    /// ADDRESSING. A peer is its Ed25519 public key: no IP, no port, no NAT
    /// in sight. Both peers connect OUT to the relay (always possible) and
    /// the relay forwards frames between them by key.
    ///
    /// PRIVACY. The relay forwards opaque bytes. Every PadForge payload is
    /// already sealed by the unchanged handshake + LinkSession AEAD, so the
    /// operator sees ciphertext. The relay keypair here is ephemeral per
    /// session and unrelated to the peer-trust identity keys.
    /// </summary>
    public sealed class IrohRelayClient : IDisposable
    {
        /// <summary>The default n0 relays (iroh/src/defaults.rs prod). Tried
        /// in order; all peers must use the same relay to exchange frames, so
        /// the chosen host is carried in the rendezvous payload.</summary>
        public static readonly string[] DefaultRelays =
        {
            "use1-1.relay.n0.iroh.link",
            "usw1-1.relay.n0.iroh.link",
            "euc1-1.relay.n0.iroh.link",
            "aps1-1.relay.n0.iroh.link",
        };

        private const string SubProtocol = "iroh-relay-v2";
        private const string DomainSep = "iroh-relay handshake v1 challenge signature";

        private const byte TagServerChallenge = 0;
        private const byte TagClientAuth = 1;
        private const byte TagServerConfirmsAuth = 2;
        private const byte TagServerDeniesAuth = 3;
        private const byte TagClientToRelayDatagram = 4;
        private const byte TagRelayToClientDatagram = 6;
        private const byte TagEndpointGone = 8;
        private const byte TagPing = 9;
        private const byte TagPong = 10;

        /// <summary>Held ACROSS the send, not just across its initiation. A
        /// lock cannot span an await, so the old lock published the Task and
        /// released immediately, which is exactly the overlap ClientWebSocket
        /// forbids: two datagrams issued from different threads could both be
        /// outstanding and the second throws, taking the relay lane down.</summary>
        private readonly SemaphoreSlim _sendGate = new(1, 1);
        private ClientWebSocket _ws;
        private CancellationTokenSource _cts;
        private Task _recvLoop;
        private volatile bool _connected;
        private volatile bool _disposed;
        private int _disconnectFired;

        /// <summary>This client's relay identity (Ed25519 public key). Peers
        /// address datagrams to it.</summary>
        public byte[] PublicKey { get; }
        private readonly byte[] _privateKey;

        /// <summary>Relay hostname this client is connected to.</summary>
        public string RelayHost { get; private set; }

        public bool IsConnected => _connected;

        /// <summary>Raised for every datagram forwarded to us:
        /// (32-byte source public key, payload).</summary>
        public event Action<byte[], byte[]> DatagramReceived;

        /// <summary>Raised when the relay connection drops.</summary>
        public event Action Disconnected;

        /// <summary>Random ephemeral relay identity (the caller side, which
        /// only needs to be addressable for the life of one call).</summary>
        public IrohRelayClient()
        {
            var kp = PeerCrypto.GenerateEd25519KeyPair();
            PublicKey = kp.PublicKey;
            _privateKey = kp.PrivateKey;
        }

        /// <summary>Relay identity derived from a 32-byte seed, so a HOST can
        /// listen at the key its connection code addresses (#294). The caller
        /// derives the same public key from the code and reaches the host with
        /// no lookup of any kind.</summary>
        public IrohRelayClient(byte[] seed)
        {
            if (seed is not { Length: 32 }) throw new ArgumentException("seed must be 32 bytes", nameof(seed));
            _privateKey = (byte[])seed.Clone();
            PublicKey = PeerCrypto.DeriveEd25519PublicKey(_privateKey);
        }

        /// <summary>blake3::derive_key per the iroh handshake. Verified against
        /// the official BLAKE3 vectors and, decisively, by the production
        /// relay accepting signatures over its output.</summary>
        internal static byte[] Blake3DeriveKey(string context, byte[] material)
        {
            var d = new Blake3Digest(256);
            d.Init(Blake3Parameters.Context(Encoding.UTF8.GetBytes(context)));
            d.BlockUpdate(material, 0, material.Length);
            var o = new byte[32];
            d.DoFinal(o, 0);
            return o;
        }

        /// <summary>Connects and authenticates to the first reachable relay,
        /// or the specific host given. Returns the connected host, or null.</summary>
        public async Task<string> ConnectAsync(string relayHost, CancellationToken ct)
        {
            var hosts = relayHost != null ? new[] { relayHost } : DefaultRelays;
            foreach (var host in hosts)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    if (await ConnectOneAsync(host, ct).ConfigureAwait(false))
                    {
                        RelayHost = host;
                        return host;
                    }
                }
                catch { /* next relay */ }
            }
            return null;
        }

        private async Task<bool> ConnectOneAsync(string host, CancellationToken ct)
        {
            var ws = new ClientWebSocket();
            try
            {
                return await ConnectOneCoreAsync(ws, host, ct).ConfigureAwait(false);
            }
            catch
            {
                // Any throw between construction and hand-off leaves the socket
                // (and its connection) alive: ConnectAsync's per-host catch
                // moves straight to the next relay, so a timeout on the first
                // three hosts leaked three sockets per attempt.
                try { ws.Dispose(); } catch { }
                throw;
            }
        }

        private async Task<bool> ConnectOneCoreAsync(ClientWebSocket ws, string host, CancellationToken ct)
        {
            ws.Options.AddSubProtocol(SubProtocol);
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                await ws.ConnectAsync(new Uri($"wss://{host}/relay"), timeout.Token).ConfigureAwait(false);

                var buf = new byte[65536];
                var r = await ws.ReceiveAsync(buf, timeout.Token).ConfigureAwait(false);
                if (r.Count < 17 || buf[0] != TagServerChallenge) { ws.Dispose(); return false; }

                var challenge = new byte[16];
                Array.Copy(buf, 1, challenge, 0, 16);
                var sig = PeerCrypto.Ed25519Sign(_privateKey, Blake3DeriveKey(DomainSep, challenge));

                // ClientAuth: tag + raw 32B key + serde_bytes sig (0x40 len + 64B).
                var frame = new byte[1 + 32 + 1 + 64];
                frame[0] = TagClientAuth;
                PublicKey.CopyTo(frame, 1);
                frame[33] = 64;
                sig.CopyTo(frame, 34);
                await ws.SendAsync(frame, WebSocketMessageType.Binary, true, timeout.Token).ConfigureAwait(false);

                var r2 = await ws.ReceiveAsync(buf, timeout.Token).ConfigureAwait(false);
                if (r2.Count < 1 || buf[0] != TagServerConfirmsAuth)
                {
                    // Name the two outcomes apart. A denial is a signature or
                    // key problem and retrying other relays will fail the same
                    // way; anything else is a protocol surprise worth seeing.
                    SdlDiagLog.WriteLine(r2.Count >= 1 && buf[0] == TagServerDeniesAuth
                        ? $"RELAY {host}: auth DENIED by the relay"
                        : $"RELAY {host}: unexpected handshake reply tag {(r2.Count >= 1 ? buf[0] : -1)}");
                    ws.Dispose();
                    return false;
                }
            }

            _ws = ws;
            // The receive loop's lifetime is the CLIENT's, not the caller's.
            // Linking it to the connect token killed a cached, shared client
            // the moment whoever first dialled through it cancelled their
            // token: IsConnected still read true, so the client kept being
            // handed out while nothing was reading from it, and every reply
            // vanished. Dispose is what ends this loop.
            _cts = new CancellationTokenSource();
            _connected = true;
            _recvLoop = Task.Run(() => RecvLoopAsync(_cts.Token));
            return true;
        }

        /// <summary>Sends a datagram to a peer, addressed by its 32-byte relay
        /// public key. Fire-and-forget semantics like UDP.</summary>
        public async Task<bool> SendAsync(byte[] destPublicKey, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            var ws = _ws;
            if (ws == null || !_connected || destPublicKey is not { Length: 32 }) return false;
            var frame = new byte[1 + 32 + 1 + payload.Length];
            frame[0] = TagClientToRelayDatagram;
            destPublicKey.CopyTo(frame, 1);
            frame[33] = 0; // ECN
            payload.Span.CopyTo(frame.AsSpan(34));
            // ClientWebSocket allows one outstanding send; serialize.
            try { await _sendGate.WaitAsync(ct).ConfigureAwait(false); }
            catch { return false; }
            try
            {
                await ws.SendAsync(frame, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
                return true;
            }
            catch
            {
                MarkDisconnected();
                return false;
            }
            finally { try { _sendGate.Release(); } catch { } }
        }

        private async Task RecvLoopAsync(CancellationToken ct)
        {
            var buf = new byte[70000];
            var ws = _ws;
            try
            {
                while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    // Reassemble one websocket message (may span receives).
                    int len = 0;
                    WebSocketReceiveResult r;
                    do
                    {
                        r = await ws.ReceiveAsync(new ArraySegment<byte>(buf, len, buf.Length - len), ct).ConfigureAwait(false);
                        len += r.Count;
                    } while (!r.EndOfMessage && len < buf.Length);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    if (!r.EndOfMessage)
                    {
                        // The message outgrew the buffer. Parsing the prefix
                        // would be wrong AND the next read would resume inside
                        // this message, so every following frame decoded as
                        // garbage: drain to the end and drop the message.
                        var sink = new byte[4096];
                        while (!r.EndOfMessage && ws.State == WebSocketState.Open)
                            r = await ws.ReceiveAsync(new ArraySegment<byte>(sink), ct).ConfigureAwait(false);
                        continue;
                    }
                    if (len < 1) continue;

                    switch (buf[0])
                    {
                        case TagRelayToClientDatagram when len >= 34:
                        {
                            var src = new byte[32];
                            Array.Copy(buf, 1, src, 0, 32);
                            var payload = new byte[len - 34];
                            Array.Copy(buf, 34, payload, 0, payload.Length);
                            DatagramReceived?.Invoke(src, payload);
                            break;
                        }
                        case TagPing when len >= 9:
                        {
                            var pong = new byte[9];
                            pong[0] = TagPong;
                            Array.Copy(buf, 1, pong, 1, 8);
                            await _sendGate.WaitAsync(ct).ConfigureAwait(false);
                            try
                            {
                                await ws.SendAsync(pong, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
                            }
                            finally { try { _sendGate.Release(); } catch { } }
                            break;
                        }
                        case TagEndpointGone when len >= 33:
                        {
                            // The relay is telling us a peer we addressed is no
                            // longer connected to it. The ARQ above this lane
                            // recovers on its own, so this is diagnostic only:
                            // without it, a peer that dropped off the relay was
                            // indistinguishable from one that simply went quiet.
                            SdlDiagLog.WriteLine(
                                "RELAY peer gone: " + Convert.ToHexString(buf, 1, 16));
                            break;
                        }
                        // Status frames and batches: nothing to do.
                    }
                }
            }
            catch { /* drop through to disconnect */ }
            MarkDisconnected();
        }

        /// <summary>Raises <see cref="Disconnected"/> exactly once. The receive
        /// loop and every failed send can reach it concurrently, and the old
        /// check-then-set let two threads both pass the guard and fire it
        /// twice, which re-armed the listener twice. A deliberate Dispose stays
        /// silent: the drop is not news to whoever asked for it, and firing
        /// there would re-arm the listener during shutdown.</summary>
        private void MarkDisconnected()
        {
            _connected = false;
            if (_disposed) return;
            if (Interlocked.Exchange(ref _disconnectFired, 1) != 0) return;
            Disconnected?.Invoke();
        }

        public void Dispose()
        {
            _disposed = true;
            _connected = false;
            Interlocked.Exchange(ref _disconnectFired, 1);
            try { _cts?.Cancel(); } catch { }
            try { _ws?.Dispose(); } catch { }
            // Wait briefly for the receive loop to leave, and observe its
            // outcome. It was started and never looked at again, so a fault
            // inside it went unnoticed and a disposal could return while the
            // loop was still touching the socket it just disposed.
            try { _recvLoop?.Wait(TimeSpan.FromMilliseconds(500)); } catch { }
            _cts?.Dispose();
        }
    }
}
