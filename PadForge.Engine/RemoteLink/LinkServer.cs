using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace PadForge.Engine.RemoteLink
{
    /// <summary>
    /// Socket transport for Remote Link (issue #138): a TCP control listener for
    /// the pairing handshake and a UDP socket for the input/feedback stream, the
    /// DsuMotionServer shape but bound to all interfaces with its own magic.
    /// Mirrors WebControllerServer's lifecycle (Start/Stop, DeviceConnected/
    /// DeviceDisconnected events) so InputService wires it the same way.
    ///
    /// Off by default — nothing listens until Start is called. The crypto peer
    /// identity gate (LinkConnection) runs before any RemotePeerDevice is created,
    /// so an unknown peer's input never reaches the pipeline.
    /// </summary>
    public sealed class LinkServer : IDisposable
    {
        private const int SIO_UDP_CONNRESET = unchecked((int)0x9800000C);

        private readonly PeerIdentity _identity;
        private readonly PeerTrustStore _trust;
        private readonly Func<PendingPairing, PairingApproval> _approve;
        private readonly Func<string> _nowUtc;
        private readonly byte[] _caps;

        private readonly object _lock = new();
        private readonly List<LinkPeerConnection> _connections = new();
        private TcpListener _tcp;
        private Socket _udp;
        private CancellationTokenSource _cts;
        private int _port;
        private Timer _reaper;
        private int _pendingHandshakes;

        // Internet lane (#294): active punch/control sinks routed off the shared
        // UDP socket. Additive to the LAN/TCP path: when empty (the default and
        // whenever the internet lane is off), RouteDatagram behaves exactly as
        // before. Punch datagrams (0xC2/0xC3) fan to every registered puncher
        // (each rejects a wrong nonce itself); control datagrams (0xC0/0xC1)
        // route to the channel whose id matches.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, Action<IPEndPoint, byte[]>> _punchSinks = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, Action<byte[]>> _controlSinks = new();

        // Relay lane (#294): iroh relay fallback when no punch can land. Both
        // peers behind CGNAT/symmetric NAT have NO direct path; the only route
        // is a third box both can reach outbound. FlexInput gets this from
        // iroh's hosted relays; PadForge speaks the same open protocol to the
        // same free relays (IrohRelayClient). Control datagrams demux by
        // channel id, HELLOs by nonce, sealed session datagrams by AEAD trial
        // open, mirroring the UDP loop.
        // TWO relay clients, never one. Every running instance is BOTH a host
        // (listening on the identity its own code derives) and potentially a
        // caller (dialling someone else's code, on whatever relay THAT code
        // names). A single shared client made those two roles destroy each
        // other: the dial disposed the listener's connection, and the
        // listener's restart loop disposed the dial's a few seconds later, so
        // a real two-machine connect could never survive. They are separate
        // connections with separate identities, and each live connection
        // remembers which one carries it.
        private IrohRelayClient _relayListen;   // host identity, from the code
        private IrohRelayClient _relayIdentity; // STABLE identity, for reconnect
        private IrohRelayClient _relayDial;     // ephemeral, for outgoing calls
        private readonly SemaphoreSlim _relayListenGate = new(1, 1);
        private readonly SemaphoreSlim _relayIdentityGate = new(1, 1);
        private readonly SemaphoreSlim _relayDialGate = new(1, 1);
        private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, Action<byte[], byte[]>> _relayControlSinks = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Action<byte[], uint>> _relayHelloWaiters = new();
        /// <summary>First byte of a relay HELLO (peer key announcement). The
        /// 0xC0-0xC3 space belongs to control/punch; sealed frames start
        /// (type&lt;&lt;4)|epoch with type 1..7, so 0xC4 is unclaimed.</summary>
        public const byte TagRelayHello = 0xC4;
        /// <summary>The host's answer to a HELLO, so the caller knows someone
        /// is listening on the code before it starts the handshake.</summary>
        public const byte TagRelayHelloAck = 0xC5;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, Action> _relayAckWaiters = new();
        /// <summary>Listen-side control sinks keyed by the CALLER's relay key.
        /// The code-derived channel is fixed, so keying the listen side by
        /// channel would make two simultaneous callers overwrite each other.
        /// Source keys are unique per caller, so this is collision-free.</summary>
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Action<byte[]>> _relayListenSinks = new();
        /// <summary>Callers whose handshake is already running, so the repeated
        /// HELLOs a caller sends while waiting never start a second one.</summary>
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _relayInFlight = new();
        private int _punchSinkCounter;

        // Covers the human approval too; a slowloris (stalled handshake) still times
        // out, and the concurrency cap bounds how many can pend (and pop dialogs) at once.
        private const int HandshakeTimeoutSeconds = 180;
        private const int MaxPendingHandshakes = 8;
        private static readonly long IdleDropTicks = System.Diagnostics.Stopwatch.Frequency * 15; // 15s of no datagrams

        public event Action<RemotePeerDevice> DeviceConnected;
        public event Action<RemotePeerDevice> DeviceDisconnected;
        // A status CODE, not English text: the App maps it to a localized string. Engine
        // can't reach the App's resources, so it must not emit user-facing prose (#138 F35).
        public event Action<LinkStatus> StatusChanged;

        public enum LinkStatusKind { Listening, Stopped, StartFailed, PeerTimedOut, ConnectFailed, LinkRejected, PeerConnected }

        public readonly struct LinkStatus
        {
            public LinkStatusKind Kind { get; }
            public int Port { get; }
            public string Peer { get; }       // short fingerprint, for PeerTimedOut / PeerConnected
            public int DeviceCount { get; }   // for PeerConnected
            public string Message { get; }    // exception text (already runtime English) for *Failed / Rejected
            public LinkStatus(LinkStatusKind kind, int port = 0, string peer = null, int deviceCount = 0, string message = null)
            { Kind = kind; Port = port; Peer = peer; DeviceCount = deviceCount; Message = message; }
        }

        /// <summary>A paired peer sent reverse output (rumble / DualSense effect packet)
        /// for one of THIS PC's shared devices (issue #138 M2). Args: peer fingerprint,
        /// the slot id (this PC's exposed-device index), and the raw OutputEffectCodec
        /// payload. InputService maps the slot to the physical device and drives it.</summary>
        public event Action<string, byte, byte[]> OutputReceived;

        /// <summary>Raised on the OWNER when a consumer reports live demand
        /// for one of our shared devices' demand-gated sources (#241 NFC).
        /// Args: peer fingerprint, link slot, payload ([0] = demand kind).</summary>
        public event Action<string, byte, byte[]> SourceDemandReceived;

        /// <summary>A paired peer sent a speaker PCM block (issue #138) for one of THIS
        /// PC's shared pads. Args: peer fingerprint, link slot, raw PCM block.</summary>
        public event Action<string, byte, byte[]> AudioReceived;

        /// <summary>Supplies the local devices to expose to a peer. Used by inbound
        /// (responder) connections so both sides share their controllers, not just the
        /// one that initiated. The outbound path passes its list to ConnectAsync.</summary>
        public Func<IReadOnlyList<RemotePeerDeviceInfo>> ExposeProvider { get; set; }

        public LinkServer(PeerIdentity identity, PeerTrustStore trust, Func<PendingPairing, PairingApproval> approve,
            byte[] capabilities = null, Func<string> nowUtcProvider = null)
        {
            _identity = identity ?? throw new ArgumentNullException(nameof(identity));
            _trust = trust ?? throw new ArgumentNullException(nameof(trust));
            _approve = approve ?? (_ => (PairingApproval)false);
            _caps = capabilities ?? new byte[] { 1, 0 };
            _nowUtc = nowUtcProvider ?? (() => DateTime.UtcNow.ToString("o"));
        }

        public bool IsRunning { get; private set; }
        public int Port => _port;

        /// <summary>True when at least one peer has a live session (someone may be
        /// consuming our shared devices). Used to keep the poll loop at full rate so
        /// shared-device input is sampled smoothly, not at the idle ~20 Hz.</summary>
        public bool HasConnections { get { lock (_lock) return _connections.Count > 0; } }

        // Diagnostics (test/telemetry): datagrams the UDP loop saw vs successfully opened.
        public int DiagDatagramsReceived;
        public int DiagDatagramsOpened;
        public int DiagDatagramsSent;
        public int DiagOutputSent;      // reverse-feedback frames we sealed+sent (#138 M2)
        public int DiagOutputReceived;  // reverse-feedback frames we opened+surfaced
        public int DiagDemandSent;      // source-demand frames we sealed+sent (#241)
        public int DiagDemandReceived;  // source-demand frames we opened+surfaced (#241)
        public int DiagAudioReceived;   // speaker PCM blocks we opened+surfaced (#138)
        public string DiagLastError;

        // STUN probe state (#294 step 1). The probe reuses the ONE bound UDP
        // socket, because a NAT mapping is per socket: the mapped endpoint is
        // only valid for the socket that asked. Responses arrive on the shared
        // receive loop, which routes any STUN Binding success (matched by the
        // RFC 5389 magic cookie plus a live transaction id) to the pending
        // probe instead of trying to open it as a session.
        private volatile TaskCompletionSource<IPEndPoint> _stunPending;
        private volatile byte[] _stunTxId;
        private volatile IPEndPoint _stunServer;
        // One probe at a time: two concurrent probes interleaving their
        // pending/txId writes could cross-wire responses between servers and
        // misclassify the NAT (adversarial review finding 2).
        private readonly SemaphoreSlim _stunGate = new(1, 1);

        /// <summary>This socket's public endpoint from the last successful STUN
        /// probe, or null if never probed / no server answered.</summary>
        public IPEndPoint PublicEndpoint { get; private set; }

        /// <summary>True when the last probe saw two servers report different
        /// mapped ports for this socket: endpoint-dependent (symmetric) NAT,
        /// where plain UDP hole punching won't work. The UI pre-warns.</summary>
        public bool IsHardNat { get; private set; }

        /// <summary>
        /// Learns this socket's public endpoint via STUN (#294 step 1), reusing
        /// the bound UDP socket so the mapping matches the one a punch would use.
        /// Probes two independent servers to classify hard NAT. Sends are done
        /// through the socket directly; responses come back through the shared
        /// receive loop (<see cref="RouteStunResponse"/>). Safe to call while the
        /// server is running and connections are live: STUN datagrams demux from
        /// sealed session datagrams by the magic cookie.
        /// </summary>
        /// <summary>This socket's classified NAT behaviour from the last probe
        /// (#294), used to decide punch strategy. Null until probed.</summary>
        public NatProfile Nat { get; private set; }

        private Timer _natKeepalive;
        private IPEndPoint _stunKeepaliveServer;
        private int _keepaliveTicks;

        /// <summary>Raised when the STUN keepalive observes a DIFFERENT public
        /// endpoint, so the host can re-mint and re-show this PC's connection
        /// code (a stale code points at a dead port and can never be punched).</summary>
        public event Action<IPEndPoint> PublicEndpointChanged;

        /// <summary>
        /// Keeps this socket's NAT mapping alive and its external port STABLE
        /// while Remote Link runs (#294).
        ///
        /// MEASURED on a real Verizon CGNAT (2026-08-10): an idle UDP mapping
        /// survives 20 s but is GONE by 40 s. Without this, the public endpoint
        /// baked into a shared code dies within a minute of minting: the peer
        /// sprays a closed port, and our probes reach them from a new port their
        /// port-restricted filter drops, so both directions fail with zero
        /// inbound probes. That was the observed field failure.
        ///
        /// A refresh also PINS the port: re-probing the same socket returns the
        /// same mapping, so a code shared minutes ago still works.
        /// </summary>
        public void StartNatKeepalive(TimeSpan? interval = null)
        {
            // 15 s sits comfortably under the measured 20 s floor, with room
            // for a lost datagram before the mapping could lapse.
            var period = interval ?? TimeSpan.FromSeconds(15);
            _natKeepalive?.Dispose();
            _keepaliveTicks = 0;
            SdlDiagLog.WriteLine($"STUN keepalive: armed every {period.TotalSeconds:0}s (measured CGNAT expiry: alive@20s, gone@40s)");
            _natKeepalive = new Timer(_ => { _ = NatKeepaliveTickAsync(); }, null, period, period);
        }

        private async Task NatKeepaliveTickAsync()
        {
            try
            {
                var sock = _udp;
                if (sock == null) return;
                // Never fight a full classification probe; skip this tick.
                if (!await _stunGate.WaitAsync(0).ConfigureAwait(false)) return;
                try
                {
                    var server = _stunKeepaliveServer;
                    if (server == null)
                    {
                        foreach (var (host, port) in StunClient.DefaultServers)
                        {
                            try
                            {
                                var addrs = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork).ConfigureAwait(false);
                                if (addrs.Length > 0) { server = new IPEndPoint(addrs[0], port); break; }
                            }
                            catch { }
                        }
                        _stunKeepaliveServer = server;
                    }
                    if (server == null) return;

                    var ep = await OneStunProbeAsync(sock, server, CancellationToken.None).ConfigureAwait(false);
                    int tick = Interlocked.Increment(ref _keepaliveTicks);
                    if (ep == null)
                    {
                        SdlDiagLog.WriteLine($"STUN keepalive tick {tick}: no response (mapping may lapse)");
                        return;
                    }
                    var prev = PublicEndpoint;
                    PublicEndpoint = ep;
                    if (prev != null && !prev.Equals(ep))
                    {
                        SdlDiagLog.WriteLine($"STUN keepalive: public endpoint MOVED {prev} -> {ep} (re-minting code)");
                        PublicEndpointChanged?.Invoke(ep);
                    }
                    // Liveness heartbeat once a minute: proves the mapping is
                    // being held (and at which port) without flooding the ring.
                    else if (tick % 4 == 1)
                        SdlDiagLog.WriteLine($"STUN keepalive tick {tick}: mapping held at {ep}");
                }
                finally { _stunGate.Release(); }
            }
            catch { /* keepalive must never take the link down */ }
        }

        public async Task<StunResult> ProbePublicEndpointAsync(CancellationToken ct = default)
        {
            var sock = _udp;
            if (sock == null) return null;

            await _stunGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Probe ALL servers (not just two) IN ORDER so the mapping's
                // per-destination behaviour is classifiable: equal ports = cone,
                // stepped ports = sequential-symmetric (predictable, punchable),
                // erratic = random-symmetric (needs a relay).
                IPEndPoint first = null;
                var observedPorts = new List<int>();
                IPAddress addr = null;

                foreach (var (host, port) in StunClient.DefaultServers)
                {
                    if (ct.IsCancellationRequested) break;
                    IPAddress[] addrs;
                    try { addrs = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct).ConfigureAwait(false); }
                    catch { continue; }
                    if (addrs.Length == 0) continue;
                    var server = new IPEndPoint(addrs[0], port);

                    var ep = await OneStunProbeAsync(sock, server, ct).ConfigureAwait(false);
                    if (ep == null) continue;
                    first ??= ep;
                    addr ??= ep.Address;
                    observedPorts.Add(ep.Port);
                }

                if (first == null) return null;

                var profile = NatProfile.Classify(addr, observedPorts);
                bool hardNat = profile.Kind == NatKind.SymmetricRandom; // only random symmetric is truly un-punchable

                // Only meaningful for the socket that asked: Stop() clears these
                // so a restarted socket never advertises the old mapping.
                PublicEndpoint = first;
                IsHardNat = hardNat;
                Nat = profile;
                SdlDiagLog.WriteLine($"STUN profile: kind={profile.Kind} public={first} delta={profile.Delta} ports=[{string.Join(",", observedPorts)}]");
                return new StunResult { PublicEndpoint = first, IsHardNat = hardNat };
            }
            finally { _stunGate.Release(); }
        }

        private async Task<IPEndPoint> OneStunProbeAsync(Socket sock, IPEndPoint server, CancellationToken ct)
        {
            // ONE transaction id across the retransmits (RFC 5389 §7.2.1): a
            // fresh id per attempt rejected any response slower than one
            // attempt window (finding 4).
            var req = StunClient.BuildBindingRequest(out var txId);
            var tcs = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
            _stunTxId = txId;
            _stunServer = server;
            _stunPending = tcs;
            try
            {
                for (int attempt = 0; attempt < 3 && !ct.IsCancellationRequested; attempt++)
                {
                    try { await sock.SendToAsync(req, SocketFlags.None, server, ct).ConfigureAwait(false); }
                    catch { return null; }

                    using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timer.CancelAfter(TimeSpan.FromMilliseconds(600));
                    try { return await tcs.Task.WaitAsync(timer.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* retransmit */ }
                    catch { return null; }
                }
                return null;
            }
            finally { _stunPending = null; _stunTxId = null; _stunServer = null; }
        }

        /// <summary>Routes an inbound datagram that is a STUN Binding response to
        /// the pending probe. Returns true if consumed (the receive loop then
        /// skips its session-open attempt). Matched by the SOURCE ADDRESS of the
        /// queried server plus the magic cookie plus the live transaction id, so
        /// neither a data datagram nor an off-path injection from another
        /// address can complete the probe (finding 5).</summary>
        private bool RouteStunResponse(ReadOnlySpan<byte> datagram, IPEndPoint from)
        {
            var pending = _stunPending;
            var txId = _stunTxId;
            var server = _stunServer;
            if (pending == null || txId == null || server == null) return false;
            if (datagram.Length < 20) return false;
            if (!from.Address.Equals(server.Address)) return false;
            // Cheap cookie precheck before the full parse.
            if (datagram[4] != 0x21 || datagram[5] != 0x12 || datagram[6] != 0xA4 || datagram[7] != 0x42)
                return false;
            var ep = StunClient.ParseBindingResponse(datagram, txId);
            if (ep == null) return false;
            pending.TrySetResult(ep);
            return true;
        }

        public bool Start(int port)
        {
            if (IsRunning) return true;
            _port = port;
            _cts = new CancellationTokenSource();

            try
            {
                _tcp = new TcpListener(IPAddress.Any, port);
                _tcp.Start();

                _udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                try { _udp.IOControl(SIO_UDP_CONNRESET, new byte[4], null); } catch { /* non-Windows */ }
                _udp.Bind(new IPEndPoint(IPAddress.Any, port));
                // Report the port actually bound. With port 0 (ephemeral) the
                // requested value is meaningless, and every candidate we
                // advertise is built from Port, so a peer would be told to
                // punch ":0".
                if (_udp.LocalEndPoint is IPEndPoint bound) _port = bound.Port;
            }
            catch (Exception ex)
            {
                // Port already held (stale socket / another process): tear the partial start
                // down so we don't leak a live listener that re-collides on the next enable,
                // and report failure so the caller can null us out (#138 F05).
                try { _tcp?.Stop(); } catch { }
                try { _udp?.Close(); } catch { }
                try { _cts?.Dispose(); } catch { }
                _tcp = null; _udp = null; _cts = null;
                DiagLastError = "start: " + ex.Message;
                StatusChanged?.Invoke(new LinkStatus(LinkStatusKind.StartFailed, message: ex.Message));
                return false;
            }

            IsRunning = true;
            // Start the loops inline so the accept/receive registrations happen
            // synchronously here (deferring to Task.Run made them late under load).
            // ConfigureAwait(false) on their awaits keeps the continuations — and the
            // approval callback — off the UI thread, so there is no deadlock.
            _ = AcceptLoopAsync(_cts.Token);
            _ = UdpLoopAsync(_cts.Token);
            // 3s tick: keepalive (so a quiet-but-live connection isn't reaped, and the
            // responder learns the initiator's endpoint even with no input flowing),
            // then reap genuinely dead ones.
            _reaper = new Timer(_ => { try { SendKeepalives(); ReapDeadConnections(); MaintainPaths(); } catch { } }, null, 3000, 3000);
            StatusChanged?.Invoke(new LinkStatus(LinkStatusKind.Listening, port: port));
            return true;
        }

        public void Stop()
        {
            if (!IsRunning) return;
            IsRunning = false;
            // The STUN-learned mapping belongs to the socket being closed; a
            // restarted socket gets a different mapping, so never let the old
            // one be advertised (finding 8).
            PublicEndpoint = null;
            IsHardNat = false;
            Nat = null;
            try { _natKeepalive?.Dispose(); } catch { }
            _natKeepalive = null;
            _stunKeepaliveServer = null;
            try { _cts.Cancel(); } catch { }
            _reaper?.Dispose(); _reaper = null;
            LinkPeerConnection[] conns;
            lock (_lock) { conns = _connections.ToArray(); _connections.Clear(); }
            foreach (var c in conns) DropConnection(c);
            try { _tcp?.Stop(); } catch { }
            try { _udp?.Close(); } catch { }
            // Null both, like the failed-Start path does. Every send path reads
            // the field and checks it for null, so a closed-but-non-null socket
            // turned each of those into a caught ObjectDisposedException
            // instead of the clean "not running" answer they test for.
            _tcp = null;
            _udp = null;
            try { _relayListen?.Dispose(); } catch { }
            try { _relayIdentity?.Dispose(); } catch { }
            try { _relayDial?.Dispose(); } catch { }
            _relayListen = null;
            _relayIdentity = null;
            _relayDial = null;
            _cts?.Dispose();
            // Null like the failed-Start path: ConnectAsync reads _cts?.Token, and
            // a disposed-but-non-null CTS throws ObjectDisposedException there.
            _cts = null;
            StatusChanged?.Invoke(new LinkStatus(LinkStatusKind.Stopped));
        }

        // ── Relay-to-direct upgrade (#294) ──────────────────────────────────
        // A session that established over the relay keeps working, but pays an
        // extra hop for every frame. Once a direct path becomes possible (the
        // peers move onto one LAN, or a NAT relaxes) the session should take
        // it. iroh does exactly this; without it a relayed link stays relayed
        // even with the two machines side by side. The relay carries the
        // signalling, so the simultaneity a punch needs is free.

        /// <summary>Our current candidate endpoints for a peer to punch.</summary>
        private List<IPEndPoint> PathCandidates()
        {
            var list = new List<IPEndPoint>();
            var pub = PublicEndpoint;
            if (pub != null) list.Add(pub);
            foreach (var lan in LocalAddresses())
            {
                var ep = new IPEndPoint(lan, _port);
                if (!list.Contains(ep)) list.Add(ep);
            }
            return list;
        }

        internal static byte[] EncodeCandidates(IReadOnlyList<IPEndPoint> eps)
        {
            var buf = new List<byte>();
            byte n = (byte)Math.Min(eps.Count, 16);
            buf.Add(n);
            for (int i = 0; i < n; i++)
            {
                var ip = eps[i].Address.GetAddressBytes();
                buf.Add((byte)ip.Length);
                buf.AddRange(ip);
                var port = new byte[2];
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(port, (ushort)eps[i].Port);
                buf.AddRange(port);
            }
            return buf.ToArray();
        }

        internal static List<IPEndPoint> DecodeCandidates(byte[] payload)
        {
            var eps = new List<IPEndPoint>();
            if (payload == null || payload.Length < 1) return eps;
            int o = 0, n = payload[o++];
            for (int i = 0; i < n; i++)
            {
                if (o >= payload.Length) break;
                int len = payload[o++];
                if (len != 4 && len != 16) break;
                if (o + len + 2 > payload.Length) break;
                var ip = new IPAddress(payload.AsSpan(o, len).ToArray()); o += len;
                ushort port = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(o)); o += 2;
                eps.Add(new IPEndPoint(ip, port));
            }
            return eps;
        }

        private void SendPathOffer(LinkPeerConnection c)
        {
            try
            {
                var cands = PathCandidates();
                if (cands.Count == 0) return;
                ulong ts = (ulong)(System.Diagnostics.Stopwatch.GetTimestamp() * (1_000_000.0 / System.Diagnostics.Stopwatch.Frequency));
                System.Threading.Interlocked.Exchange(ref c.LastOfferTicks, System.Diagnostics.Stopwatch.GetTimestamp());
                SendSealed(c, c.DataSession.Seal(LinkMessageType.PathOffer, 0, ts, EncodeCandidates(cands)));
            }
            catch (Exception ex) { DiagLastError = "pathoffer-send: " + ex.Message; }
        }

        private void OnPathOffer(LinkPeerConnection c, byte[] payload)
        {
            var peerCands = DecodeCandidates(payload);
            if (peerCands.Count == 0) return;

            // Answer with our own candidates unless we just offered, so the
            // peer punches at the same time without an offer ping-pong.
            long since = System.Diagnostics.Stopwatch.GetTimestamp() - System.Threading.Interlocked.Read(ref c.LastOfferTicks);
            if (since > System.Diagnostics.Stopwatch.Frequency * 3) SendPathOffer(c);

            if (System.Threading.Interlocked.Exchange(ref c.UpgradeRunning, 1) != 0) return;
            _ = Task.Run(async () =>
            {
                try { await TryUpgradeAsync(c, peerCands).ConfigureAwait(false); }
                catch (Exception ex) { DiagLastError = "pathupgrade: " + ex.Message; }
                finally { System.Threading.Interlocked.Exchange(ref c.UpgradeRunning, 0); }
            });
        }

        private async Task TryUpgradeAsync(LinkPeerConnection c, IReadOnlyList<IPEndPoint> peerCandidates)
        {
            var sock = _udp;
            if (sock == null || c.PathNonce == null || c.PeerUdpEndpoint != null) return;

            var adapter = new UdpPunchAdapter(sock);
            int key = Interlocked.Increment(ref _punchSinkCounter);
            _punchSinks[key] = (from, dg) => adapter.OnDatagram?.Invoke(from, dg);
            try
            {
                var self = new List<IPEndPoint>();
                if (PublicEndpoint != null) self.Add(PublicEndpoint);
                foreach (var lan in LocalAddresses()) self.Add(new IPEndPoint(lan, _port));

                var puncher = new HolePuncher(adapter, c.PathNonce, sprayInterval: null,
                    selfEndpoints: self, selfFingerprint: _identity?.Fingerprint);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts?.Token ?? CancellationToken.None);
                cts.CancelAfter(TimeSpan.FromSeconds(8));
                IPEndPoint won;
                try { won = await puncher.PunchAsync(peerCandidates, cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { won = null; }
                if (won == null) return;

                // Take the direct path. The relay client stays on the
                // connection, so a direct path that later dies falls back.
                System.Threading.Interlocked.Exchange(ref c.LastDirectTicks, System.Diagnostics.Stopwatch.GetTimestamp());
                c.PeerUdpEndpoint = won;
                SdlDiagLog.WriteLine($"PATH upgraded: relay -> direct {won} for peer {Short(c.PeerFingerprintHex)}");
            }
            finally { _punchSinks.TryRemove(key, out _); }
        }

        /// <summary>Runs on the reaper tick: offer a path on relayed sessions,
        /// and drop back to the relay when an upgraded direct path goes quiet.</summary>
        private void MaintainPaths()
        {
            LinkPeerConnection[] conns;
            lock (_lock) conns = _connections.ToArray();
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            long freq = System.Diagnostics.Stopwatch.Frequency;
            foreach (var c in conns)
            {
                if (c.RelayClient == null || c.RelayPeerKey == null) continue; // not a relayed session
                if (c.PeerUdpEndpoint == null)
                {
                    // Still on the relay: offer a path every ~15 s.
                    if (now - System.Threading.Interlocked.Read(ref c.LastOfferTicks) > freq * 15)
                        SendPathOffer(c);
                }
                else
                {
                    // Upgraded: if the direct path has been silent, fall back.
                    long last = System.Threading.Interlocked.Read(ref c.LastDirectTicks);
                    if (last != 0 && now - last > freq * 6)
                    {
                        c.PeerUdpEndpoint = null;
                        System.Threading.Interlocked.Exchange(ref c.LastOfferTicks, 0);
                        SdlDiagLog.WriteLine($"PATH downgraded: direct went quiet, back to relay for peer {Short(c.PeerFingerprintHex)}");
                    }
                }
            }
        }

        private void SendKeepalives()
        {
            LinkPeerConnection[] conns;
            lock (_lock) conns = _connections.ToArray();
            foreach (var c in conns)
            {
                try { SendSealed(c, c.DataSession.Seal(LinkMessageType.Keepalive, 0, 0, Array.Empty<byte>())); }
                catch { }
            }
        }

        private void ReapDeadConnections()
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            LinkPeerConnection[] dead;
            lock (_lock)
            {
                dead = _connections.Where(c => now - System.Threading.Interlocked.Read(ref c.LastActivityTicks) > IdleDropTicks).ToArray();
                foreach (var c in dead) _connections.Remove(c);
            }
            foreach (var c in dead) { DropConnection(c); StatusChanged?.Invoke(new LinkStatus(LinkStatusKind.PeerTimedOut, peer: Short(c.PeerFingerprintHex))); }
        }

        /// <summary>Initiate an outbound pairing/reconnect to a peer, optionally exposing local devices.</summary>
        public async Task<bool> ConnectAsync(string host, int port, IReadOnlyList<RemotePeerDeviceInfo> exposeLocal, CancellationToken ct = default)
        {
            // IPv4 to match the IPv4 UDP data socket (the DsuMotionServer model);
            // a default dual-stack TcpClient yields IPv4-mapped-IPv6 endpoints the
            // IPv4 UDP socket can't SendTo.
            var client = new TcpClient(AddressFamily.InterNetwork);
            bool registered = false;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(HandshakeTimeoutSeconds));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts?.Token ?? CancellationToken.None, timeout.Token);
                await client.ConnectAsync(host, port, linked.Token).ConfigureAwait(false);
                var channel = new TcpControlChannel(client.GetStream());
                var result = await LinkConnection.RunInitiatorAsync(channel, _identity, _trust, exposeLocal, _caps, _approve, _nowUtc(), linked.Token).ConfigureAwait(false);

                var peerIp = ((IPEndPoint)client.Client.RemoteEndPoint).Address;
                if (peerIp.IsIPv4MappedToIPv6) peerIp = peerIp.MapToIPv4();
                Register(result, client, new IPEndPoint(peerIp, port), exposeLocal);
                registered = true;
                return true;
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke(new LinkStatus(LinkStatusKind.ConnectFailed, message: ex.Message));
                return false;
            }
            finally
            {
                if (!registered) { try { client.Dispose(); } catch { } }
            }
        }

        // ── Internet lane: punch + handshake over the shared UDP socket (#294) ──
        //
        // Additive to ConnectAsync (TCP/LAN). The socket adapters send on _udp
        // and receive via the RouteDatagram demux above; a session registers its
        // sinks for the duration of the punch+handshake, then deregisters. On
        // success the peer is Register()ed with the punched UDP endpoint, so the
        // sealed data plane flows exactly as on the LAN.

        private sealed class UdpPunchAdapter : IPunchTransport
        {
            private readonly Socket _sock;
            public Action<IPEndPoint, byte[]> OnDatagram { get; set; }
            public UdpPunchAdapter(Socket s) { _sock = s; }
            public async Task SendToAsync(byte[] dg, IPEndPoint dest, CancellationToken ct)
            {
                try { await _sock.SendToAsync(dg, SocketFlags.None, dest, ct).ConfigureAwait(false); }
                catch (ObjectDisposedException) { } catch (SocketException) { }
            }
        }

        private sealed class UdpControlAdapter : IDatagramTransport
        {
            private readonly Socket _sock;
            private readonly Func<IPEndPoint> _peer;
            public Action<byte[]> OnDatagram { get; set; }
            public UdpControlAdapter(Socket s, Func<IPEndPoint> peer) { _sock = s; _peer = peer; }
            public async Task SendAsync(byte[] dg, CancellationToken ct)
            {
                var ep = _peer();
                if (ep == null) return;
                try { await _sock.SendToAsync(dg, SocketFlags.None, ep, ct).ConfigureAwait(false); }
                catch (ObjectDisposedException) { } catch (SocketException) { }
            }
        }

        /// <summary>Two-way internet-lane connect (#294): BOTH peers call this
        /// with the OTHER's candidate endpoints, a shared nonce, and complementary
        /// handshake roles (the lower fingerprint passes handshakeAsInitiator
        /// true). Both spray and both listen, so both NATs open even when neither
        /// is full-cone. Returns true on success (the peer is Registered), false
        /// on punch failure so the caller can fall back to Connect by Address.
        /// Held open up to <paramref name="punchTimeout"/> so a human delay
        /// between the two people clicking Connect still lands.</summary>
        public async Task<bool> ConnectByPunchAsync(
            IReadOnlyList<IPEndPoint> candidates, byte[] sharedNonce, bool handshakeAsInitiator,
            IReadOnlyList<RemotePeerDeviceInfo> exposeLocal, TimeSpan? punchTimeout = null,
            CancellationToken ct = default)
            => await PunchConnectAsync(handshakeAsInitiator, candidates, sharedNonce, exposeLocal,
                punchTimeout ?? TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);

        // Nonces we are already auto-responding to, so a spray (many probes per
        // second) starts exactly one responder session.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _autoResponding = new();

        /// <summary>Capability-derived nonces for paired peers, so an unsolicited
        /// DHT-lane dial is recognised too. Set by the host each maintenance
        /// pass; empty is fine (the code lane needs no registration).</summary>
        public void SetKnownPunchNonces(IEnumerable<byte[]> nonces)
        {
            _knownNonces.Clear();
            if (nonces == null) return;
            foreach (var n in nonces)
                if (n != null && n.Length == HolePuncher.NonceLen)
                    _knownNonces[Convert.ToHexString(n)] = 0;
        }
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _knownNonces = new();

        /// <summary>
        /// Answers an unsolicited punch probe (#294). The probe carries the
        /// DIALER's fingerprint prefix, so we can derive the very nonce the two
        /// of us would share and verify it. On a match we start a responder
        /// punch/handshake aimed at the probe's SOURCE endpoint, which is
        /// authoritative (better than any advertised candidate). This is what
        /// makes the flow one-sided: the host just leaves Remote Link running.
        /// </summary>
        private void TryAutoRespondToPunch(IPEndPoint from, byte[] dg)
        {
            try
            {
                if (!HolePuncher.TryParseProbe(dg, out _, out var senderPrefix, out var nonce)) return;
                if (_identity?.Fingerprint == null) return;

                // Either the nonce two peers derive from each other's codes, or
                // a paired peer's capability nonce (the DHT lane).
                var expected = LinkCode.TwoWayPunchNonce(_identity.Fingerprint, senderPrefix);
                bool match = System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expected, nonce)
                             || _knownNonces.ContainsKey(Convert.ToHexString(nonce));
                if (!match) return;

                string key = Convert.ToHexString(nonce);
                if (!_autoResponding.TryAdd(key, 0)) return; // one session per nonce

                bool asInitiator = LinkCode.IsHandshakeInitiator(_identity.Fingerprint, senderPrefix);
                SdlDiagLog.WriteLine($"PUNCH auto-respond: dial from {from}, role={(asInitiator ? "init" : "resp")}");
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var expose = ExposeProvider?.Invoke() ?? Array.Empty<RemotePeerDeviceInfo>();
                        await PunchConnectAsync(asInitiator, new[] { from }, nonce, expose,
                            TimeSpan.FromSeconds(20), _cts?.Token ?? CancellationToken.None).ConfigureAwait(false);
                    }
                    catch { }
                    finally { _autoResponding.TryRemove(key, out _); }
                });
            }
            catch { }
        }

        /// <summary>Every local IPv4 this machine holds, so the punch can
        /// recognise its own endpoints (self-connect guard).</summary>
        private static IEnumerable<IPAddress> LocalAddresses()
        {
            List<IPAddress> list = new();
            try
            {
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                            list.Add(ua.Address);
                }
            }
            catch { }
            return list;
        }

        private async Task<bool> PunchConnectAsync(
            bool isInitiator, IReadOnlyList<IPEndPoint> candidates, byte[] sharedNonce,
            IReadOnlyList<RemotePeerDeviceInfo> exposeLocal, TimeSpan punchTimeout, CancellationToken ct)
        {
            var sock = _udp;
            if (sock == null || sharedNonce == null || sharedNonce.Length != 16) return false;

            var punchAdapter = new UdpPunchAdapter(sock);
            IPEndPoint peerEp = null;
            var controlAdapter = new UdpControlAdapter(sock, () => peerEp);
            uint channelId = UdpControlChannel.ChannelIdFromNonce(sharedNonce);

            int probesIn = 0;
            int punchKey = Interlocked.Increment(ref _punchSinkCounter);
            _punchSinks[punchKey] = (from, dg) =>
            {
                // Learn the peer endpoint from the first punch datagram so the
                // control adapter knows where to send once the handshake starts.
                if (peerEp == null) { peerEp = from; SdlDiagLog.WriteLine($"PUNCH {(isInitiator ? "init" : "resp")}: first probe from {from}"); }
                System.Threading.Interlocked.Increment(ref probesIn);
                punchAdapter.OnDatagram?.Invoke(from, dg);
            };
            _controlSinks[channelId] = dg => controlAdapter.OnDatagram?.Invoke(dg);
            string candStr = candidates == null ? "(none)" : string.Join(",", candidates.Select(c => c.ToString()));
            SdlDiagLog.WriteLine($"PUNCH {(isInitiator ? "init" : "resp")}: start chan={channelId:X8} candidates=[{candStr}]");
            try
            {
                using var timeout = new CancellationTokenSource(punchTimeout + TimeSpan.FromSeconds(HandshakeTimeoutSeconds + 5));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts?.Token ?? CancellationToken.None, timeout.Token);
                var expose = exposeLocal ?? ExposeProvider?.Invoke() ?? Array.Empty<RemotePeerDeviceInfo>();

                // Our own endpoints, so the punch can never target or accept
                // itself. Two machines behind ONE router share a public IP, so
                // the peer's "public" candidate can be our own mapping; a
                // hairpinning router would then deliver our probe back to us
                // carrying the shared nonce and the punch would settle on self.
                var self = new List<IPEndPoint>();
                if (PublicEndpoint != null) self.Add(PublicEndpoint);
                foreach (var lan in LocalAddresses()) self.Add(new IPEndPoint(lan, _port));

                // Both sides spray the peer's candidates AND listen; the role
                // only chooses who leads the handshake once the path is open.
                var punched = await PunchedConnection.ConnectTwoWayAsync(
                    punchAdapter, controlAdapter, sharedNonce, candidates, isInitiator,
                    _identity, _trust, expose, _caps, _approve, _nowUtc(), punchTimeout, linked.Token,
                    selfEndpoints: self).ConfigureAwait(false);

                if (punched?.Connection == null)
                {
                    SdlDiagLog.WriteLine($"PUNCH {(isInitiator ? "init" : "resp")}: FAILED, {probesIn} inbound probes seen (0 = peer's NAT dropped our probes or peer not armed)");
                    return false;
                }
                SdlDiagLog.WriteLine($"PUNCH {(isInitiator ? "init" : "resp")}: connected via {punched.PeerEndpoint}");
                Register(punched.Connection, client: null, peerUdpEndpoint: punched.PeerEndpoint, exposeLocal: expose);
                return true;
            }
            catch (Exception ex)
            {
                SdlDiagLog.WriteLine($"PUNCH {(isInitiator ? "init" : "resp")}: exception {ex.GetType().Name} {ex.Message}");
                StatusChanged?.Invoke(new LinkStatus(LinkStatusKind.ConnectFailed, message: ex.Message));
                return false;
            }
            finally
            {
                _punchSinks.TryRemove(punchKey, out _);
                _controlSinks.TryRemove(channelId, out _);
            }
        }

        /// <summary>Our OUTGOING relay identity, published in the rendezvous
        /// record so a host can reach us when no punch lands. Null until
        /// <see cref="EnsureDialRelayAsync"/> succeeds.</summary>
        public byte[] RelayPublicKey => _relayDial?.PublicKey;
        /// <summary>The relay host our outgoing client is connected to.</summary>
        public string RelayHostName => _relayDial?.RelayHost;

        /// <summary>Ensures the LISTENING relay connection: authenticated as
        /// the identity derived from this host's own connection code, so
        /// callers who know the code can address us with no lookup. Independent
        /// of the outgoing client, so dialling a peer never tears this down.</summary>
        public async Task<string> EnsureListenRelayAsync(string relayHost, byte[] identitySeed, CancellationToken ct)
        {
            if (identitySeed is not { Length: 32 }) return null;
            await _relayListenGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var wantKey = PeerCrypto.DeriveEd25519PublicKey(identitySeed);
                var live = _relayListen;
                if (live is { IsConnected: true }
                    && live.PublicKey.AsSpan().SequenceEqual(wantKey)
                    && (relayHost == null || string.Equals(live.RelayHost, relayHost, StringComparison.OrdinalIgnoreCase)))
                    return live.RelayHost;
                try { live?.Dispose(); } catch { }
                _relayListen = null;
                var client = new IrohRelayClient(identitySeed);
                client.DatagramReceived += OnRelayDatagram;
                var host = await client.ConnectAsync(relayHost, ct).ConfigureAwait(false);
                if (host == null) { client.Dispose(); return null; }
                _relayListen = client;
                SdlDiagLog.WriteLine($"RELAY listen-connected: {host} as {Convert.ToHexString(client.PublicKey).Substring(0, 16)}");
                return host;
            }
            catch { return null; }
            finally { _relayListenGate.Release(); }
        }

        /// <summary>Ensures the OUTGOING relay connection (ephemeral identity),
        /// used to dial a peer. Independent of the listening client, so an
        /// incoming call is never disturbed by an outgoing one.</summary>
        public async Task<string> EnsureDialRelayAsync(string relayHost, CancellationToken ct)
        {
            await _relayDialGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var live = _relayDial;
                if (live is { IsConnected: true }
                    && (relayHost == null || string.Equals(live.RelayHost, relayHost, StringComparison.OrdinalIgnoreCase)))
                    return live.RelayHost;
                try { live?.Dispose(); } catch { }
                _relayDial = null;
                var client = new IrohRelayClient();
                client.DatagramReceived += OnRelayDatagram;
                var host = await client.ConnectAsync(relayHost, ct).ConfigureAwait(false);
                if (host == null) { client.Dispose(); return null; }
                _relayDial = client;
                SdlDiagLog.WriteLine($"RELAY dial-connected: {host} as {Convert.ToHexString(client.PublicKey).Substring(0, 16)}");
                return host;
            }
            catch { return null; }
            finally { _relayDialGate.Release(); }
        }

        /// <summary>
        /// HOST half of the code-derived relay lane (#294). Connects to the
        /// relay AS the identity the host's own connection code addresses and
        /// waits there for a caller. No lookup is involved: the caller derives
        /// the same key from the code it dialled, so it can reach this host
        /// directly. This replaces carrying the caller's relay key over the
        /// DHT, which required the two machines' DHT views to converge and so
        /// failed across different ISPs.
        ///
        /// Runs until cancelled, handling one caller at a time. Each accepted
        /// call runs the UNMODIFIED handshake, exactly like every other lane.
        /// </summary>
        public async Task ListenOnCodeRelayAsync(Dht.CodeRendezvous.RelayRendezvous rdv, CancellationToken ct)
        {
            if (rdv == null) return;
            var host = await EnsureListenRelayAsync(rdv.Host, rdv.PrivateKey, ct).ConfigureAwait(false);
            var relay = _relayListen;
            if (host == null || relay == null) return;
            SdlDiagLog.WriteLine($"RELAY listen: on {host} as {Convert.ToHexString(rdv.PublicKey)} chan={rdv.Channel:X8}");

            // Return when the relay socket drops. The wait below is on a HELLO
            // that only the relay can deliver, so a dropped connection left
            // this loop parked forever and the host unreachable for the rest of
            // the session: the caller's own reconnect wrapper only re-arms when
            // this method RETURNS, and nothing else could make it.
            var dropped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Action onDropped = () => dropped.TrySetResult(true);
            relay.Disconnected += onDropped;
            try
            {
            while (!ct.IsCancellationRequested)
            {
                if (!relay.IsConnected) break;
                // Wait for a caller's HELLO addressed to our code identity.
                var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                string waitKey = "code:" + rdv.Channel.ToString("X8");
                _relayHelloWaiters[waitKey] = (k, _) => tcs.TrySetResult(k);
                byte[] callerKey;
                try
                {
                    var winner = await Task.WhenAny(tcs.Task, dropped.Task).WaitAsync(ct).ConfigureAwait(false);
                    if (winner != tcs.Task) break;
                    callerKey = tcs.Task.Result;
                }
                catch (OperationCanceledException) { break; }
                finally { _relayHelloWaiters.TryRemove(waitKey, out _); }

                // Handle the call on its own task and go straight back to
                // waiting. Awaiting it here meant one pending call held the
                // whole listener: a first-contact pairing prompt waits on a
                // human, and until they answered the host could accept nobody
                // else (observed live, 2026-08-11).
                string callerHex = Convert.ToHexString(callerKey);
                if (!_relayInFlight.TryAdd(callerHex, 0)) continue; // retransmitted HELLO
                SdlDiagLog.WriteLine($"RELAY listen: caller {callerHex.Substring(0, 16)}");
                // No token on Task.Run: a cancelled token makes it never run
                // the body, so the finally never removes the in-flight key and
                // that caller could never be accepted again. The body honors
                // the token itself.
                _ = Task.Run(async () =>
                {
                    try { await AcceptCodeRelayCallAsync(rdv, callerKey, ct).ConfigureAwait(false); }
                    catch (Exception ex) { SdlDiagLog.WriteLine($"RELAY listen: call failed {ex.GetType().Name} {ex.Message}"); }
                    finally { _relayInFlight.TryRemove(callerHex, out _); }
                });
            }
            }
            finally { relay.Disconnected -= onDropped; }
        }

        private async Task AcceptCodeRelayCallAsync(
            Dht.CodeRendezvous.RelayRendezvous rdv, byte[] callerKey, CancellationToken ct)
        {
            var relay = _relayListen;
            if (relay == null) return;

            // The host answers the HELLO so the caller knows it is present,
            // then both run the handshake on the code-derived channel. Roles
            // come from the two relay keys, which both sides know by now.
            var ack = new byte[1 + 4];
            ack[0] = TagRelayHelloAck;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ack.AsSpan(1), rdv.Channel);
            for (int i = 0; i < 3; i++)
                await relay.SendAsync(callerKey, ack, ct).ConfigureAwait(false);

            bool asInitiator = CompareKeys(rdv.PublicKey, callerKey) < 0;
            var adapter = new RelayControlAdapter(relay, callerKey);
            string callerHex = Convert.ToHexString(callerKey);
            _relayListenSinks[callerHex] = dg => adapter.OnDatagram?.Invoke(dg);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                // Generous: first contact shows a pairing prompt and waits on a
                // person. The accept loop is no longer blocked meanwhile, so a
                // long wait here costs nothing.
                timeout.CancelAfter(TimeSpan.FromMinutes(3));
                var expose = ExposeProvider?.Invoke() ?? Array.Empty<RemotePeerDeviceInfo>();
                var result = await PunchedConnection.ConnectRelayOnChannelAsync(
                    adapter, rdv.Channel, asInitiator,
                    _identity, _trust, expose, _caps, _approve, _nowUtc(), timeout.Token).ConfigureAwait(false);
                if (result == null) { SdlDiagLog.WriteLine("RELAY listen: handshake did not complete"); return; }
                SdlDiagLog.WriteLine("RELAY listen: handshake complete");
                Register(result, client: null, peerUdpEndpoint: null, exposeLocal: expose,
                    relayPeerKey: callerKey, relayClient: relay);
            }
            finally { _relayListenSinks.TryRemove(callerHex, out _); }
        }

        /// <summary>
        /// CALLER half of the code-derived relay lane (#294): connect to the
        /// relay the code names, HELLO the host's code-derived key until it
        /// acknowledges, then run the UNMODIFIED handshake on the code's
        /// channel. No lookup, so this works wherever the relay is reachable.
        /// </summary>
        public async Task<bool> ConnectByCodeRelayAsync(
            Dht.CodeRendezvous.RelayRendezvous rdv, IReadOnlyList<RemotePeerDeviceInfo> exposeLocal,
            TimeSpan? timeout = null, CancellationToken ct = default)
        {
            if (rdv == null) return false;
            // The caller keeps its own ephemeral identity (only the host needs
            // the derived one), so two callers never collide on the relay.
            var host = await EnsureDialRelayAsync(rdv.Host, ct).ConfigureAwait(false);
            var relay = _relayDial;
            if (host == null || relay == null) return false;

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts?.Token ?? CancellationToken.None);
            linked.CancelAfter(timeout ?? TimeSpan.FromSeconds(45));
            SdlDiagLog.WriteLine($"RELAY dial: {host} host-key={Convert.ToHexString(rdv.PublicKey).Substring(0, 16)} chan={rdv.Channel:X8}");

            // HELLO until the host acknowledges, so a host that armed a moment
            // later still gets the call.
            var hello = new byte[1 + 4];
            hello[0] = TagRelayHello;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(hello.AsSpan(1), rdv.Channel);
            var ackTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _relayAckWaiters[rdv.Channel] = () => ackTcs.TrySetResult(true);
            try
            {
                using var helloCts = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                var hct = helloCts.Token;
                _ = Task.Run(async () =>
                {
                    while (!hct.IsCancellationRequested)
                    {
                        await relay.SendAsync(rdv.PublicKey, hello, hct).ConfigureAwait(false);
                        try { await Task.Delay(700, hct).ConfigureAwait(false); } catch { break; }
                    }
                });
                try { await ackTcs.Task.WaitAsync(linked.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { SdlDiagLog.WriteLine("RELAY dial: host never answered"); return false; }
                finally { helloCts.Cancel(); }
                SdlDiagLog.WriteLine("RELAY dial: host answered, handshaking");

                bool asInitiator = CompareKeys(relay.PublicKey, rdv.PublicKey) < 0;
                var adapter = new RelayControlAdapter(relay, rdv.PublicKey);
                string hostHex = Convert.ToHexString(rdv.PublicKey);
                _relayControlSinks[rdv.Channel] = (src, dg) =>
                {
                    if (Convert.ToHexString(src) == hostHex) adapter.OnDatagram?.Invoke(dg);
                };
                try
                {
                    var expose = exposeLocal ?? ExposeProvider?.Invoke() ?? Array.Empty<RemotePeerDeviceInfo>();
                    var result = await PunchedConnection.ConnectRelayOnChannelAsync(
                        adapter, rdv.Channel, asInitiator,
                        _identity, _trust, expose, _caps, _approve, _nowUtc(), linked.Token).ConfigureAwait(false);
                    if (result == null) { SdlDiagLog.WriteLine("RELAY dial: handshake did not complete"); return false; }
                    SdlDiagLog.WriteLine("RELAY dial: handshake complete");
                    Register(result, client: null, peerUdpEndpoint: null, exposeLocal: expose,
                        relayPeerKey: rdv.PublicKey, relayClient: relay);
                    return true;
                }
                finally { _relayControlSinks.TryRemove(rdv.Channel, out _); }
            }
            catch (Exception ex)
            {
                SdlDiagLog.WriteLine($"RELAY dial: exception {ex.GetType().Name} {ex.Message}");
                return false;
            }
            finally { _relayAckWaiters.TryRemove(rdv.Channel, out _); }
        }

        /// <summary>Deterministic ordering of two relay keys, so the two sides
        /// pick complementary handshake roles with no negotiation.</summary>
        private static int CompareKeys(byte[] a, byte[] b)
        {
            int n = Math.Min(a.Length, b.Length);
            for (int i = 0; i < n; i++) if (a[i] != b[i]) return a[i].CompareTo(b[i]);
            return a.Length.CompareTo(b.Length);
        }

        /// <summary>Ensures the STABLE identity relay connection: authenticated
        /// as the identity derived from this machine's own long-term public
        /// key. Separate from the code listener, which moves whenever the code
        /// is re-minted, and from the dial client.</summary>
        public async Task<string> EnsureIdentityRelayAsync(string relayHost, byte[] identitySeed, CancellationToken ct)
        {
            if (identitySeed is not { Length: 32 }) return null;
            await _relayIdentityGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var wantKey = PeerCrypto.DeriveEd25519PublicKey(identitySeed);
                var live = _relayIdentity;
                if (live is { IsConnected: true } && live.PublicKey.AsSpan().SequenceEqual(wantKey))
                    return live.RelayHost;
                try { live?.Dispose(); } catch { }
                _relayIdentity = null;
                var client = new IrohRelayClient(identitySeed);
                client.DatagramReceived += OnRelayDatagram;
                var host = await client.ConnectAsync(relayHost, ct).ConfigureAwait(false);
                if (host == null) { client.Dispose(); return null; }
                _relayIdentity = client;
                SdlDiagLog.WriteLine($"RELAY identity-connected: {host} as {Convert.ToHexString(client.PublicKey).Substring(0, 16)}");
                return host;
            }
            catch { return null; }
            finally { _relayIdentityGate.Release(); }
        }

        /// <summary>
        /// Listens on this machine's STABLE identity relay so a PAIRED peer can
        /// reconnect after either side restarts. Unlike the code listener this
        /// address never changes, and unlike the DHT presence lane it needs no
        /// lookup and no punch, so it works across ISPs and behind CGNAT.
        /// Serves every paired peer at once: each pair talks on the channel
        /// their shared capability derives.
        /// </summary>
        public async Task ListenOnIdentityRelayAsync(Dht.CodeRendezvous.RelayRendezvous rdv, CancellationToken ct)
        {
            if (rdv == null) return;
            var host = await EnsureIdentityRelayAsync(rdv.Host, rdv.PrivateKey, ct).ConfigureAwait(false);
            var relay = _relayIdentity;
            if (host == null || relay == null) return;
            SdlDiagLog.WriteLine($"RELAY identity listen: on {host} as {Convert.ToHexString(rdv.PublicKey)}");

            // Same drop contract as the code listener: return so the caller's
            // wrapper re-arms, instead of waiting forever on a HELLO that a
            // dead relay can never deliver. This lane is the RECONNECT address,
            // so wedging it costs a paired peer its way back in.
            var dropped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Action onDropped = () => dropped.TrySetResult(true);
            relay.Disconnected += onDropped;
            try
            {
            while (!ct.IsCancellationRequested)
            {
                if (!relay.IsConnected) break;
                var tcs = new TaskCompletionSource<(byte[] key, uint chan)>(TaskCreationOptions.RunContinuationsAsynchronously);
                _relayHelloWaiters["identity"] = (k, c) => tcs.TrySetResult((k, c));
                (byte[] key, uint chan) call;
                try
                {
                    var winner = await Task.WhenAny(tcs.Task, dropped.Task).WaitAsync(ct).ConfigureAwait(false);
                    if (winner != tcs.Task) break;
                    call = tcs.Task.Result;
                }
                catch (OperationCanceledException) { break; }
                finally { _relayHelloWaiters.TryRemove("identity", out _); }

                string callerHex = Convert.ToHexString(call.key);
                if (!_relayInFlight.TryAdd(callerHex, 0)) continue;
                SdlDiagLog.WriteLine($"RELAY identity listen: caller {callerHex.Substring(0, 16)} chan={call.chan:X8}");
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await AcceptRelayCallAsync(relay, rdv.PublicKey, call.key, call.chan, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) { SdlDiagLog.WriteLine($"RELAY identity listen: call failed {ex.GetType().Name} {ex.Message}"); }
                    finally { _relayInFlight.TryRemove(callerHex, out _); }
                });
            }
            }
            finally { relay.Disconnected -= onDropped; }
        }

        /// <summary>Dials a PAIRED peer at its stable identity relay, on the
        /// channel their shared capability derives. This is the reconnect path
        /// that needs neither a DHT lookup nor a successful punch.</summary>
        public async Task<bool> ConnectByIdentityRelayAsync(
            byte[] peerIdentityPublicKey, byte[] capability,
            IReadOnlyList<RemotePeerDeviceInfo> exposeLocal, TimeSpan? timeout = null, CancellationToken ct = default)
        {
            var rdv = Dht.CodeRendezvous.DeriveIdentityRelay(peerIdentityPublicKey);
            if (rdv == null || capability == null) return false;
            uint chan = Dht.CodeRendezvous.ChannelForCapability(capability);

            var host = await EnsureDialRelayAsync(rdv.Host, ct).ConfigureAwait(false);
            var relay = _relayDial;
            if (host == null || relay == null) return false;

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts?.Token ?? CancellationToken.None);
            linked.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
            SdlDiagLog.WriteLine($"RELAY identity dial: {host} peer={Convert.ToHexString(rdv.PublicKey).Substring(0, 16)} chan={chan:X8}");

            var hello = new byte[1 + 4];
            hello[0] = TagRelayHello;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(hello.AsSpan(1), chan);
            var ackTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _relayAckWaiters[chan] = () => ackTcs.TrySetResult(true);
            try
            {
                using var helloCts = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                var hct = helloCts.Token;
                _ = Task.Run(async () =>
                {
                    while (!hct.IsCancellationRequested)
                    {
                        await relay.SendAsync(rdv.PublicKey, hello, hct).ConfigureAwait(false);
                        try { await Task.Delay(700, hct).ConfigureAwait(false); } catch { break; }
                    }
                });
                try { await ackTcs.Task.WaitAsync(linked.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { SdlDiagLog.WriteLine("RELAY identity dial: peer not listening"); return false; }
                finally { helloCts.Cancel(); }

                bool asInitiator = CompareKeys(relay.PublicKey, rdv.PublicKey) < 0;
                var adapter = new RelayControlAdapter(relay, rdv.PublicKey);
                string peerHex = Convert.ToHexString(rdv.PublicKey);
                _relayControlSinks[chan] = (src, dg) =>
                {
                    if (Convert.ToHexString(src) == peerHex) adapter.OnDatagram?.Invoke(dg);
                };
                try
                {
                    var expose = exposeLocal ?? ExposeProvider?.Invoke() ?? Array.Empty<RemotePeerDeviceInfo>();
                    var result = await PunchedConnection.ConnectRelayOnChannelAsync(
                        adapter, chan, asInitiator,
                        _identity, _trust, expose, _caps, _approve, _nowUtc(), linked.Token).ConfigureAwait(false);
                    if (result == null) { SdlDiagLog.WriteLine("RELAY identity dial: handshake did not complete"); return false; }
                    SdlDiagLog.WriteLine("RELAY identity dial: reconnected");
                    Register(result, client: null, peerUdpEndpoint: null, exposeLocal: expose,
                        relayPeerKey: rdv.PublicKey, relayClient: relay);
                    return true;
                }
                finally { _relayControlSinks.TryRemove(chan, out _); }
            }
            catch (Exception ex)
            {
                SdlDiagLog.WriteLine($"RELAY identity dial: exception {ex.GetType().Name} {ex.Message}");
                return false;
            }
            finally { _relayAckWaiters.TryRemove(chan, out _); }
        }

        /// <summary>Shared accept half for any listening relay identity.</summary>
        private async Task AcceptRelayCallAsync(
            IrohRelayClient relay, byte[] myKey, byte[] callerKey, uint chan, CancellationToken ct)
        {
            if (relay == null) return;
            var ack = new byte[1 + 4];
            ack[0] = TagRelayHelloAck;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ack.AsSpan(1), chan);
            for (int i = 0; i < 3; i++)
                await relay.SendAsync(callerKey, ack, ct).ConfigureAwait(false);

            bool asInitiator = CompareKeys(myKey, callerKey) < 0;
            var adapter = new RelayControlAdapter(relay, callerKey);
            string callerHex = Convert.ToHexString(callerKey);
            _relayListenSinks[callerHex] = dg => adapter.OnDatagram?.Invoke(dg);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromMinutes(3));
                var expose = ExposeProvider?.Invoke() ?? Array.Empty<RemotePeerDeviceInfo>();
                var result = await PunchedConnection.ConnectRelayOnChannelAsync(
                    adapter, chan, asInitiator,
                    _identity, _trust, expose, _caps, _approve, _nowUtc(), timeout.Token).ConfigureAwait(false);
                if (result == null) { SdlDiagLog.WriteLine("RELAY accept: handshake did not complete"); return; }
                SdlDiagLog.WriteLine("RELAY accept: handshake complete");
                Register(result, client: null, peerUdpEndpoint: null, exposeLocal: expose,
                    relayPeerKey: callerKey, relayClient: relay);
            }
            finally { _relayListenSinks.TryRemove(callerHex, out _); }
        }

        /// <summary>Relay-lane connect (#294): the UNMODIFIED authenticated
        /// handshake over an iroh relay, for when no direct path can be
        /// punched. The side that read the caller's relay key from the
        /// rendezvous record passes it and announces itself with HELLOs; the
        /// other side passes null and learns the key from the first HELLO's
        /// source. Everything downstream (device lists, sealed data plane,
        /// trust) is identical to the punched path.</summary>
        public async Task<bool> ConnectByRelayAsync(
            string relayHost, byte[] peerRelayKey, byte[] sharedNonce, bool handshakeAsInitiator,
            IReadOnlyList<RemotePeerDeviceInfo> exposeLocal, TimeSpan? timeout = null, CancellationToken ct = default)
        {
            if (sharedNonce is not { Length: 16 }) return false;
            var host = await EnsureDialRelayAsync(relayHost, ct).ConfigureAwait(false);
            var relay = _relayDial;
            if (host == null || relay == null) return false;

            uint channelId = UdpControlChannel.ChannelIdFromNonce(sharedNonce);
            string nonceHex = Convert.ToHexString(sharedNonce);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts?.Token ?? CancellationToken.None);
            linked.CancelAfter(timeout ?? TimeSpan.FromSeconds(45));
            SdlDiagLog.WriteLine($"RELAY connect: start via {host} role={(handshakeAsInitiator ? "init" : "resp")} peerKey={(peerRelayKey == null ? "(await hello)" : Convert.ToHexString(peerRelayKey).Substring(0, 16))}");

            byte[] peerKey = peerRelayKey;
            System.Threading.CancellationTokenSource helloCts = null;
            try
            {
                if (peerKey == null)
                {
                    // Wait for the peer's HELLO to learn its relay key.
                    var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _relayHelloWaiters[nonceHex] = (k, _) => tcs.TrySetResult(k);
                    try { peerKey = await tcs.Task.WaitAsync(linked.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { SdlDiagLog.WriteLine("RELAY connect: no HELLO within the window"); return false; }
                    finally { _relayHelloWaiters.TryRemove(nonceHex, out _); }
                    SdlDiagLog.WriteLine($"RELAY connect: HELLO from {Convert.ToHexString(peerKey).Substring(0, 16)}");
                }
                else
                {
                    // Announce ourselves until the handshake completes: the
                    // waiter side may still be inside its own punch window.
                    helloCts = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                    var hello = new byte[1 + sharedNonce.Length];
                    hello[0] = TagRelayHello;
                    sharedNonce.CopyTo(hello, 1);
                    var hct = helloCts.Token;
                    var helloKey = peerKey;
                    _ = Task.Run(async () =>
                    {
                        while (!hct.IsCancellationRequested)
                        {
                            await relay.SendAsync(helloKey, hello, hct).ConfigureAwait(false);
                            try { await Task.Delay(1000, hct).ConfigureAwait(false); } catch { break; }
                        }
                    });
                }

                var adapter = new RelayControlAdapter(relay, peerKey);
                string peerKeyHex = Convert.ToHexString(peerKey);
                _relayControlSinks[channelId] = (src, dg) =>
                {
                    if (Convert.ToHexString(src) == peerKeyHex) adapter.OnDatagram?.Invoke(dg);
                };

                var expose = exposeLocal ?? ExposeProvider?.Invoke() ?? Array.Empty<RemotePeerDeviceInfo>();
                var result = await PunchedConnection.ConnectRelayAsync(
                    adapter, sharedNonce, handshakeAsInitiator,
                    _identity, _trust, expose, _caps, _approve, _nowUtc(), linked.Token).ConfigureAwait(false);
                if (result == null)
                {
                    SdlDiagLog.WriteLine("RELAY connect: handshake did not complete");
                    return false;
                }
                SdlDiagLog.WriteLine($"RELAY connect: handshake complete via {host}");
                Register(result, client: null, peerUdpEndpoint: null, exposeLocal: expose,
                    relayPeerKey: peerKey, relayClient: relay);
                return true;
            }
            catch (Exception ex)
            {
                SdlDiagLog.WriteLine($"RELAY connect: exception {ex.GetType().Name} {ex.Message}");
                StatusChanged?.Invoke(new LinkStatus(LinkStatusKind.ConnectFailed, message: ex.Message));
                return false;
            }
            finally
            {
                try { helloCts?.Cancel(); } catch { }
                helloCts?.Dispose();
                _relayControlSinks.TryRemove(channelId, out _);
            }
        }

        /// <summary>Everything the relay forwards to us: HELLOs (peer key
        /// announcements), control datagrams for an in-flight handshake, and
        /// sealed session datagrams for a registered connection. Mirrors
        /// RouteDatagram's demux for the relay lane.</summary>
        private void OnRelayDatagram(byte[] src, byte[] dg)
        {
            try
            {
                if (dg == null || dg.Length == 0 || src is not { Length: 32 }) return;
                byte tag = dg[0];
                if (tag == TagRelayHello && dg.Length >= 17)
                {
                    // Nonce-addressed HELLO (the DHT-assisted lane).
                    string key = Convert.ToHexString(dg, 1, 16);
                    if (_relayHelloWaiters.TryGetValue(key, out var waiter)) waiter(src, 0);
                    return;
                }
                if (tag == TagRelayHello && dg.Length >= 5)
                {
                    // Channel-addressed HELLO. Delivered to the waiter for the
                    // channel when one exists (the code lane, one fixed
                    // channel), else to the identity listener's wildcard
                    // waiter, which serves every paired peer on its own
                    // capability-derived channel.
                    uint chan = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(dg.AsSpan(1));
                    if (_relayHelloWaiters.TryGetValue("code:" + chan.ToString("X8"), out var w)) { w(src, chan); return; }
                    if (_relayHelloWaiters.TryGetValue("identity", out var iw)) iw(src, chan);
                    return;
                }
                if (tag == TagRelayHelloAck && dg.Length >= 5)
                {
                    uint chan = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(dg.AsSpan(1));
                    if (_relayAckWaiters.TryGetValue(chan, out var ack)) ack();
                    return;
                }
                if ((tag == UdpControlChannel.TagData || tag == UdpControlChannel.TagAck) && dg.Length >= 9)
                {
                    // Listen side first: keyed by the caller's own key, so it is
                    // unambiguous even with several callers on the one channel.
                    if (_relayListenSinks.TryGetValue(Convert.ToHexString(src), out var lsink)) { lsink(dg); return; }
                    uint chan = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(dg.AsSpan(1));
                    if (_relayControlSinks.TryGetValue(chan, out var sink)) sink(src, dg);
                    return;
                }
                // Sealed session datagram over the relay lane. No endpoint to
                // learn: relayed arrivals never teach a UDP address.
                LinkPeerConnection[] conns;
                lock (_lock) conns = _connections.ToArray();
                foreach (var c in conns)
                    if (TryDispatchSession(c, dg, from: null)) return;
            }
            catch (Exception ex) { DiagLastError = "relay-recv: " + ex.Message; }
        }

        /// <summary>IDatagramTransport over the relay lane: control datagrams
        /// to one peer key. Inbound is fed by OnRelayDatagram via the
        /// channel-id sink, filtered to the expected source key.</summary>
        private sealed class RelayControlAdapter : IDatagramTransport
        {
            private readonly IrohRelayClient _relay;
            private readonly byte[] _peerKey;
            public RelayControlAdapter(IrohRelayClient relay, byte[] peerKey) { _relay = relay; _peerKey = peerKey; }
            public Action<byte[]> OnDatagram { get; set; }
            public async Task SendAsync(byte[] datagram, CancellationToken ct)
                => await _relay.SendAsync(_peerKey, datagram, ct).ConfigureAwait(false);
        }

        /// <summary>Sends one sealed datagram over whichever transport the
        /// connection rides: the punched/LAN UDP path when the peer's endpoint
        /// is known, else the relay lane. False when neither is usable yet.</summary>
        private bool SendSealed(LinkPeerConnection c, byte[] dg)
        {
            var ep = c.PeerUdpEndpoint;
            if (ep != null)
            {
                _udp.SendTo(dg, ep);
                return true;
            }
            var relay = c.RelayClient ?? _relayDial ?? _relayListen;
            var key = c.RelayPeerKey;
            if (relay != null && key != null)
            {
                _ = relay.SendAsync(key, dg, CancellationToken.None);
                return true;
            }
            return false;
        }

        /// <summary>Fingerprints of peers with a live session right now (for UI state).</summary>
        public IReadOnlyList<string> ConnectedFingerprints()
        {
            lock (_lock)
                return _connections.Select(c => c.PeerFingerprintHex)
                    .Where(f => !string.IsNullOrEmpty(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Sever any live session with a peer (called on revocation). Its
        /// devices go offline and its UDP datagrams stop routing (no session opens them).</summary>
        public void RevokePeer(string fingerprintHex)
        {
            LinkPeerConnection[] conns;
            lock (_lock)
            {
                conns = _connections.Where(c => string.Equals(c.PeerFingerprintHex, fingerprintHex, StringComparison.OrdinalIgnoreCase)).ToArray();
                foreach (var c in conns) _connections.Remove(c);
            }
            foreach (var c in conns) DropConnection(c);
        }

        /// <summary>Seal and send one exposed local device's state to every peer consuming it.</summary>
        public void PushLocalFrame(byte slot, CustomInputState state, CustomInputStateCodec.Caps caps, ulong timestampUs)
        {
            LinkPeerConnection[] conns;
            lock (_lock) conns = _connections.ToArray();
            // Snapshot BEFORE encoding: with zero peers the encode (two
            // allocations per call at 125 Hz per shared device) was pure
            // waste.
            if (conns.Length == 0) return;
            byte[] payload = CustomInputStateCodec.Encode(state, caps);
            foreach (var c in conns)
            {
                try
                {
                    byte[] dg = c.DataSession.Seal(LinkMessageType.Input, slot, timestampUs, payload);
                    if (!SendSealed(c, dg)) { DiagLastError = "push: peer transport not learned yet"; continue; }
                    System.Threading.Interlocked.Increment(ref DiagDatagramsSent);
                }
                catch (Exception ex) { DiagLastError = "push: " + ex.Message; }
            }
        }

        /// <summary>Send one reverse output frame (issue #138 M2) to the peer that OWNS
        /// the device this output is for. The slot is the device's link slot
        /// (<see cref="RemotePeerDevice.LinkSlot"/>), and payload is an OutputEffectCodec
        /// blob. Addressed by the owning peer's fingerprint so multi-peer setups route
        /// each device's feedback back to the correct owner.</summary>
        public void PushOutputEffect(string peerFingerprint, byte slot, byte[] payload)
        {
            if (string.IsNullOrEmpty(peerFingerprint) || payload == null) return;
            LinkPeerConnection[] conns;
            lock (_lock) conns = _connections.ToArray();
            ulong ts = (ulong)(System.Diagnostics.Stopwatch.GetTimestamp() * (1_000_000.0 / System.Diagnostics.Stopwatch.Frequency));
            // Prefer a matching connection whose endpoint is already learned; a duplicate
            // (e.g. a half-open responder side) with a null endpoint must not shadow the
            // live one. Bailing on the first match the way the input push does would drop
            // every output frame whenever a stale connection sorts first.
            bool matched = false;
            foreach (var c in conns)
            {
                if (!string.Equals(c.PeerFingerprintHex, peerFingerprint, StringComparison.OrdinalIgnoreCase)) continue;
                matched = true;
                try
                {
                    if (!SendSealed(c, c.DataSession.Seal(LinkMessageType.Output, slot, ts, payload))) continue;
                    System.Threading.Interlocked.Increment(ref DiagDatagramsSent);
                    System.Threading.Interlocked.Increment(ref DiagOutputSent);
                }
                catch (Exception ex) { DiagLastError = "output: " + ex.Message; }
                return;
            }
            if (matched) DiagLastError = "output: peer endpoint not learned yet";
        }

        /// <summary>Tell the device's OWNER that a live mapping on this side is
        /// polling a demand-gated source (#241 NFC reader). Same addressing as
        /// <see cref="PushOutputEffect"/> on its own datagram type. Fire it on
        /// the consumer's demand cadence: the owner treats each arrival as a
        /// fresh stamp and lets it lapse, so a deleted or disabled binding
        /// stops arming the hardware without needing an explicit "off".</summary>
        public void PushSourceDemand(string peerFingerprint, byte slot, byte[] payload)
        {
            if (string.IsNullOrEmpty(peerFingerprint) || payload == null) return;
            LinkPeerConnection[] conns;
            lock (_lock) conns = _connections.ToArray();
            ulong ts = (ulong)(System.Diagnostics.Stopwatch.GetTimestamp() * (1_000_000.0 / System.Diagnostics.Stopwatch.Frequency));
            foreach (var c in conns)
            {
                if (!string.Equals(c.PeerFingerprintHex, peerFingerprint, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    // Same diag trailer as the PushOutputEffect twin (audit
                    // 2026-07-25, C32): a demand dropped for an unlearned
                    // transport was invisible in diagnostics.
                    if (!SendSealed(c, c.DataSession.Seal(LinkMessageType.SourceDemand, slot, ts, payload)))
                    { DiagLastError = "demand: peer transport not learned yet"; continue; }
                    System.Threading.Interlocked.Increment(ref DiagDatagramsSent);
                    System.Threading.Interlocked.Increment(ref DiagDemandSent);
                }
                catch (Exception ex) { DiagLastError = "demand: " + ex.Message; }
                return;
            }
        }

        /// <summary>Send one speaker PCM block (issue #138) to the peer that owns the
        /// device. Same addressing as <see cref="PushOutputEffect"/> but on the Audio
        /// datagram type so the owner renders it to the pad speaker.</summary>
        public void PushAudio(string peerFingerprint, byte slot, byte[] payload)
        {
            if (string.IsNullOrEmpty(peerFingerprint) || payload == null) return;
            LinkPeerConnection[] conns;
            lock (_lock) conns = _connections.ToArray();
            ulong ts = (ulong)(System.Diagnostics.Stopwatch.GetTimestamp() * (1_000_000.0 / System.Diagnostics.Stopwatch.Frequency));
            foreach (var c in conns)
            {
                if (!string.Equals(c.PeerFingerprintHex, peerFingerprint, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    if (!SendSealed(c, c.DataSession.Seal(LinkMessageType.Audio, slot, ts, payload))) continue;
                    System.Threading.Interlocked.Increment(ref DiagDatagramsSent);
                }
                catch (Exception ex) { DiagLastError = "audio: " + ex.Message; }
                return;
            }
        }

        // ── Accept / handshake (responder) ──────────────────────────────────

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _tcp.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { continue; } // transient — keep listening, don't kill the loop

                // Bound concurrent in-flight handshakes (slowloris + dialog-spam DoS).
                if (System.Threading.Interlocked.Increment(ref _pendingHandshakes) > MaxPendingHandshakes)
                {
                    System.Threading.Interlocked.Decrement(ref _pendingHandshakes);
                    try { client.Dispose(); } catch { }
                    continue;
                }
                _ = HandleResponderAsync(client, ct);
            }
        }

        private async Task HandleResponderAsync(TcpClient client, CancellationToken ct)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(HandshakeTimeoutSeconds));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
                var channel = new TcpControlChannel(client.GetStream());
                // Expose our own devices to the peer too, so sharing is bidirectional.
                var expose = ExposeProvider?.Invoke() ?? (IReadOnlyList<RemotePeerDeviceInfo>)Array.Empty<RemotePeerDeviceInfo>();
                var result = await LinkConnection.RunResponderAsync(channel, _identity, _trust, expose, _caps, _approve, _nowUtc(), linked.Token).ConfigureAwait(false);
                // The peer's UDP endpoint is learned from the first inbound datagram.
                Register(result, client, peerUdpEndpoint: null, exposeLocal: expose);
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke(new LinkStatus(LinkStatusKind.LinkRejected, message: ex.Message));
                try { client.Dispose(); } catch { }
            }
            finally { System.Threading.Interlocked.Decrement(ref _pendingHandshakes); }
        }

        private void Register(LinkConnectionResult result, TcpClient client, IPEndPoint peerUdpEndpoint, IReadOnlyList<RemotePeerDeviceInfo> exposeLocal, byte[] relayPeerKey = null, IrohRelayClient relayClient = null)
        {
            // Dedup: a reconnecting peer replaces its prior connection instead of
            // stacking a second one (and leaking the old socket/devices).
            LinkPeerConnection[] dupes;
            lock (_lock)
            {
                dupes = _connections.Where(c => string.Equals(c.PeerFingerprintHex, result.PeerFingerprintHex, StringComparison.OrdinalIgnoreCase)).ToArray();
                foreach (var d in dupes) _connections.Remove(d);
            }
            foreach (var d in dupes) DropConnection(d);

            var conn = new LinkPeerConnection
            {
                DataSession = new LinkSession(result.DataKey, result.IsInitiator),
                RemoteDevices = new System.Collections.Concurrent.ConcurrentDictionary<byte, RemotePeerDevice>(),
                PeerUdpEndpoint = peerUdpEndpoint,
                RelayPeerKey = relayPeerKey,
                RelayClient = relayClient,
                PathNonce = PeerCrypto.DeriveKey(result.DataKey, salt: null,
                    System.Text.Encoding.ASCII.GetBytes("PadForge/path-upgrade/v1"), HolePuncher.NonceLen),
                Tcp = client,
                PeerFingerprintHex = result.PeerFingerprintHex,
                LastActivityTicks = System.Diagnostics.Stopwatch.GetTimestamp(),
            };
            // Key each device by the owner's STABLE slot (carried in the device list), so a
            // device hot-plugged after connect routes by a slot that never shifts (#138).
            foreach (var d in result.RemoteDevices)
            {
                d.LinkSlot = d.Info.Slot;
                d.SetConnected(d.Info.Online);
                conn.RemoteDevices[d.LinkSlot] = d;
            }
            lock (_lock) _connections.Add(conn);
            foreach (var d in conn.RemoteDevices.Values) DeviceConnected?.Invoke(d);
            StatusChanged?.Invoke(new LinkStatus(LinkStatusKind.PeerConnected, peer: Short(conn.PeerFingerprintHex), deviceCount: conn.RemoteDevices.Count));
        }

        // ── UDP receive (data + learn-endpoint) ─────────────────────────────

        private async Task UdpLoopAsync(CancellationToken ct)
        {
            // 64 KB (the UDP maximum): an oversized datagram makes ReceiveFrom
            // throw and the frame vanishes into DiagLastError, which is how a
            // too-large device-list push silently killed device sync (audit
            // F1). The send side budgets the device list under the OLD peers'
            // 4 KB buffer; receiving at the protocol maximum costs one buffer
            // and removes this failure mode for anything a peer sends.
            var buf = new byte[65536];
            var any = new IPEndPoint(IPAddress.Any, 0);
            while (!ct.IsCancellationRequested)
            {
                SocketReceiveFromResult r;
                // ConfigureAwait(false), like every other await in this
                // file and as its own banner at the top requires. Without it
                // the loop captures whatever context started it (the UI
                // thread, via the dashboard toggle), so RouteDatagram and
                // the whole decode path below ran ON THE WPF DISPATCHER,
                // one post per datagram (round 34).
                try { r = await _udp.ReceiveFromAsync(buf, SocketFlags.None, any, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex) { DiagLastError = "recv: " + ex.GetType().Name + " " + ex.Message; continue; }

                System.Threading.Interlocked.Increment(ref DiagDatagramsReceived);
                var datagram = buf.AsSpan(0, r.ReceivedBytes);
                var from = (IPEndPoint)r.RemoteEndPoint;
                RouteDatagram(datagram, from);
            }
        }

        private void RouteDatagram(ReadOnlySpan<byte> datagram, IPEndPoint from)
        {
            // A STUN Binding response for our in-flight probe (#294 step 1) is
            // consumed here, before any session-open attempt, so the probe on
            // the shared socket doesn't fight the data plane.
            if (RouteStunResponse(datagram, from)) return;

            // Internet lane (#294): punch and reliable-control datagrams demux
            // by their leading tag ahead of session routing. No-op unless an
            // internet connect is active (the sink maps are empty otherwise), so
            // the LAN/TCP data path is unchanged. A sealed LinkSession frame's
            // first byte is (type<<4)|epoch with type 1..8, never 0xC0-0xC5.
            {
                byte tag0 = datagram.Length > 0 ? datagram[0] : (byte)0;
                if (tag0 == 0xC2 || tag0 == 0xC3) // punch ping/pong
                {
                    var dg = datagram.ToArray();
                    foreach (var sink in _punchSinks.Values) sink(from, dg);
                    // ALWAYS-ON RESPONDER: a ping addressed to us with a nonce
                    // we can derive is a peer dialing our code. Answer it even
                    // with no punch in flight, so only the joiner has to click
                    // Connect. Without this the host had to click at the same
                    // moment or every probe was silently dropped (field failure
                    // 2026-08-11: probes reached a reachable peer that never
                    // replied because it was not punching).
                    if (tag0 == 0xC2) TryAutoRespondToPunch(from, dg);
                    return;
                }
            }
            if (!_punchSinks.IsEmpty || !_controlSinks.IsEmpty)
            {
                byte tag0 = datagram.Length > 0 ? datagram[0] : (byte)0;
                if ((tag0 == 0xC0 || tag0 == 0xC1) && datagram.Length >= 9) // control data/ack
                {
                    uint chan = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(datagram.Slice(1));
                    if (_controlSinks.TryGetValue(chan, out var csink)) { csink(datagram.ToArray()); return; }
                }
            }

            LinkPeerConnection[] conns;
            lock (_lock) conns = _connections.ToArray();

            // The AEAD tag identifies the owning session. Only the right
            // session opens it, and a failed open never advances a replay window.
            foreach (var c in conns)
                if (TryDispatchSession(c, datagram, from)) return;
        }

        /// <summary>Trial-opens one sealed datagram against a connection's
        /// session and dispatches it. Shared by the UDP loop and the relay
        /// lane (#294); <paramref name="from"/> is null for relayed arrivals,
        /// which never teach a UDP endpoint.</summary>
        private bool TryDispatchSession(LinkPeerConnection c, ReadOnlySpan<byte> datagram, IPEndPoint from)
        {
            {
                if (!c.DataSession.Open(datagram, out var type, out byte slot, out ulong ts, out byte[] payload))
                    return false;

                System.Threading.Interlocked.Increment(ref DiagDatagramsOpened);
                System.Threading.Interlocked.Exchange(ref c.LastActivityTicks, System.Diagnostics.Stopwatch.GetTimestamp());
                // Learn the peer's UDP endpoint on first verified datagram (responder side).
                if (from != null && c.PeerUdpEndpoint == null) c.PeerUdpEndpoint = from;
                // Direct-path liveness, so an upgraded session that loses its
                // direct route can fall back to the relay.
                if (from != null) System.Threading.Interlocked.Exchange(ref c.LastDirectTicks, System.Diagnostics.Stopwatch.GetTimestamp());

                if (type == LinkMessageType.Input)
                {
                    // Route by slot id to the matching device (the peer streams each of
                    // its devices on its own slot). Pass the send timestamp for
                    // newest-wins (the replay window accepts in-window reorders).
                    if (c.RemoteDevices.TryGetValue(slot, out var rd))
                        rd.ApplyFramePayload(payload, ts);
                }
                else if (type == LinkMessageType.Output)
                {
                    // Reverse feedback from a consumer of one of OUR shared devices.
                    // Surface it for InputService to map slot -> physical device and
                    // drive the hardware (LinkServer is Engine-side, no UserDevices).
                    System.Threading.Interlocked.Increment(ref DiagOutputReceived);
                    OutputReceived?.Invoke(c.PeerFingerprintHex, slot, payload);
                }
                else if (type == LinkMessageType.SourceDemand)
                {
                    // A consumer's live mapping wants a demand-gated source on
                    // one of OUR devices (#241). Surface it so InputService can
                    // map slot -> physical device and arm the hardware.
                    System.Threading.Interlocked.Increment(ref DiagDemandReceived);
                    SourceDemandReceived?.Invoke(c.PeerFingerprintHex, slot, payload);
                }
                else if (type == LinkMessageType.Audio)
                {
                    System.Threading.Interlocked.Increment(ref DiagAudioReceived);
                    AudioReceived?.Invoke(c.PeerFingerprintHex, slot, payload);
                }
                else if (type == LinkMessageType.PathOffer)
                {
                    // The peer published its candidate endpoints over the relay.
                    // Punch them, and answer with our own so the peer punches
                    // too: both sides must fire for a NAT to open.
                    try { OnPathOffer(c, payload); }
                    catch (Exception ex) { DiagLastError = "pathoffer: " + ex.Message; }
                }
                else if (type == LinkMessageType.DeviceList)
                {
                    // The owner's current exposed-device set: add new, remove gone, update
                    // active/inactive — so devices hot-plugged after connect appear live (#138).
                    try { ReconcileRemoteDevices(c, LinkConnection.DecodeDeviceList(payload)); }
                    catch (Exception ex) { DiagLastError = "devlist-recv: " + ex.Message; }
                }
                return true;
            }
        }

        /// <summary>Apply the owner's latest device list to a connection: add devices that
        /// appeared, drop ones that vanished, and update active/inactive on the rest. Fires
        /// DeviceConnected / DeviceDisconnected so InputService registers/unregisters them
        /// exactly as it does for the handshake set. Runs on the UDP receive thread.</summary>
        private void ReconcileRemoteDevices(LinkPeerConnection c, List<RemotePeerDeviceInfo> infos)
        {
            // Reconcile by the device's STABLE id (PeerLocalDeviceId), not its link slot. Keying
            // by slot fired DeviceConnected for the new slot then DeviceDisconnected for the old
            // one (same InstanceGuid), netting to "unregistered" and leaving a still-shared
            // device offline with no output route when a device merely changed slot (#138 F37).
            var byId = new Dictionary<string, RemotePeerDevice>(StringComparer.Ordinal);
            foreach (var d in c.RemoteDevices.Values)
                byId[d.Info.PeerLocalDeviceId ?? ""] = d;

            var next = new System.Collections.Concurrent.ConcurrentDictionary<byte, RemotePeerDevice>();
            var keptIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var info in infos)
            {
                info.PeerFingerprintHex = c.PeerFingerprintHex; // identity salted by the authenticated key
                string id = info.PeerLocalDeviceId ?? "";
                keptIds.Add(id);
                if (byId.TryGetValue(id, out var existing))
                {
                    bool slotChanged = existing.LinkSlot != info.Slot;
                    existing.LinkSlot = info.Slot;
                    existing.SetConnected(info.Online); // same device, just active/inactive (+ maybe a new slot)
                    // Refresh relayed metadata in place: the owner's named
                    // inputs grow as session-dynamic usages appear, and the
                    // periodic push is how they reach an already-registered
                    // device. Identity fields (ids, the peer-labeled Name)
                    // stay untouched. Reference swap, atomic for readers.
                    if (info.DeviceObjects != null && info.DeviceObjects.Length > 0)
                        existing.Info.DeviceObjects = info.DeviceObjects;
                    if (info.NumTouchpads > 0)
                    {
                        // Counts before the count-of-counts, so a concurrent
                        // reader that sees the new NumTouchpads also sees the
                        // matching array (readers bounds-check regardless).
                        existing.Info.TouchpadFingerCounts = info.TouchpadFingerCounts;
                        existing.Info.NumTouchpads = info.NumTouchpads;
                    }
                    if (!string.IsNullOrEmpty(info.SerialNumber))
                        existing.Info.SerialNumber = info.SerialNumber;
                    // Capability bits refresh with the metadata (audit
                    // 2026-07-25, C41): a capability appearing after
                    // connect (a Joy-Con pair joining, a reader arming
                    // rule flipping) never reached the consumer, since
                    // the fresh-registration branch below runs only for
                    // unknown device ids. Counts and flags are relayed
                    // state, not identity.
                    existing.Info.HasRumble = info.HasRumble;
                    existing.Info.HasRumbleTriggers = info.HasRumbleTriggers;
                    existing.Info.HasGyro = info.HasGyro;
                    existing.Info.HasAccel = info.HasAccel;
                    existing.Info.HasAccelAux = info.HasAccelAux;
                    existing.Info.HasGyroAux = info.HasGyroAux;
                    existing.Info.HasTouchpad = info.HasTouchpad;
                    existing.Info.HasHaptic = info.HasHaptic;
                    existing.Info.HasNfcReader = info.HasNfcReader;
                    existing.Info.NumAxes = info.NumAxes;
                    existing.Info.NumButtons = info.NumButtons;
                    existing.Info.NumHats = info.NumHats;
                    existing.Info.InputDeviceType = info.InputDeviceType;
                    next[info.Slot] = existing;
                    // Re-register only when the slot moved, so the slot-stamped output route refreshes.
                    if (slotChanged) DeviceConnected?.Invoke(existing);
                }
                else
                {
                    // Name the device under the peer it came from, e.g. "DualSense (John's PC)":
                    // the custom name we set, else the announced host name. Done once at first
                    // sight of this device id, so reconnects and slot moves keep the existing
                    // name (no double suffix). The handshake set is labeled the same way in
                    // LinkConnection, so devices present at connect carry the label too.
                    string peerLabel = _trust.ResolvePeerLabel(info.PeerFingerprintHex);
                    if (!string.IsNullOrWhiteSpace(peerLabel))
                        info.Name = $"{info.Name} ({peerLabel})";

                    var dev = new RemotePeerDevice(info) { LinkSlot = info.Slot };
                    dev.SetConnected(info.Online);
                    next[info.Slot] = dev;
                    DeviceConnected?.Invoke(dev);
                }
            }

            var old = c.RemoteDevices;
            c.RemoteDevices = next;
            // Retire only the devices whose identity truly vanished from the owner's set.
            foreach (var d in old.Values)
            {
                if (keptIds.Contains(d.Info.PeerLocalDeviceId ?? "")) continue;
                d.SetConnected(false);
                DeviceDisconnected?.Invoke(d);
                d.Dispose();
            }
        }

        /// <summary>Owner: push the current exposed-device set to every connected peer
        /// (issue #138 live device sync). Sent on change and periodically; the consumer
        /// reconciles. Each info carries its stable Slot + Online.</summary>
        public void PushDeviceList(IReadOnlyList<RemotePeerDeviceInfo> devices)
        {
            if (devices == null) return;
            LinkPeerConnection[] conns;
            lock (_lock) conns = _connections.ToArray();
            // No peers: skip the ~KB device-list encode that otherwise
            // ran every 2 s push with nobody to receive it.
            if (conns.Length == 0) return;
            byte[] payload;
            try { payload = LinkConnection.EncodeDeviceList(devices); }
            catch (Exception ex) { DiagLastError = "devlist-enc: " + ex.Message; return; }
            ulong ts = (ulong)(System.Diagnostics.Stopwatch.GetTimestamp() * (1_000_000.0 / System.Diagnostics.Stopwatch.Frequency));
            foreach (var c in conns)
            {
                try
                {
                    if (!SendSealed(c, c.DataSession.Seal(LinkMessageType.DeviceList, 0, ts, payload))) continue;
                }
                catch (Exception ex) { DiagLastError = "devlist: " + ex.Message; }
            }
        }

        private void DropConnection(LinkPeerConnection c)
        {
            foreach (var d in c.RemoteDevices.Values)
            {
                d.SetConnected(false);
                DeviceDisconnected?.Invoke(d);
                d.Dispose();
            }
            try { c.Tcp?.Dispose(); } catch { }
        }

        public void Dispose() => Stop();

        private static string Short(string fp) => string.IsNullOrEmpty(fp) ? "?" : fp.Substring(0, Math.Min(8, fp.Length));

        private sealed class LinkPeerConnection
        {
            public LinkSession DataSession;
            /// <summary>Devices the peer exposes, keyed by their stable link slot (#138 live
            /// device sync). Concurrent: the UDP loop routes + reconciles it, the reaper
            /// iterates it on drop.</summary>
            public System.Collections.Concurrent.ConcurrentDictionary<byte, RemotePeerDevice> RemoteDevices;
            public volatile IPEndPoint PeerUdpEndpoint;
            /// <summary>Set when this connection rides the relay lane: the
            /// peer's 32-byte relay public key. Sends address it, receives
            /// demux by AEAD exactly like UDP.</summary>
            public byte[] RelayPeerKey;
            /// <summary>WHICH relay connection carries this session. A session
            /// established on the listening client must keep using it even
            /// while an outgoing dial runs on the other one.</summary>
            public IrohRelayClient RelayClient;
            /// <summary>Shared punch nonce for the relay-to-direct upgrade,
            /// derived from the session data key so BOTH peers compute it with
            /// no extra exchange.</summary>
            public byte[] PathNonce;
            /// <summary>QPC of the last datagram that arrived over the DIRECT
            /// path. An upgraded session whose direct path goes quiet falls
            /// back to the relay instead of going dark.</summary>
            public long LastDirectTicks;
            /// <summary>QPC of our last PathOffer, so an offer exchange cannot
            /// ping-pong.</summary>
            public long LastOfferTicks;
            /// <summary>Set while an upgrade punch is in flight.</summary>
            public int UpgradeRunning;
            public TcpClient Tcp;
            public string PeerFingerprintHex;
            public long LastActivityTicks; // QPC; updated on each verified datagram, read by the reaper
        }
    }
}
