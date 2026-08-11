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
            _reaper = new Timer(_ => { try { SendKeepalives(); ReapDeadConnections(); } catch { } }, null, 3000, 3000);
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
            try { _cts.Cancel(); } catch { }
            _reaper?.Dispose(); _reaper = null;
            LinkPeerConnection[] conns;
            lock (_lock) { conns = _connections.ToArray(); _connections.Clear(); }
            foreach (var c in conns) DropConnection(c);
            try { _tcp?.Stop(); } catch { }
            try { _udp?.Close(); } catch { }
            _cts?.Dispose();
            // Null like the failed-Start path: ConnectAsync reads _cts?.Token, and
            // a disposed-but-non-null CTS throws ObjectDisposedException there.
            _cts = null;
            StatusChanged?.Invoke(new LinkStatus(LinkStatusKind.Stopped));
        }

        private void SendKeepalives()
        {
            LinkPeerConnection[] conns;
            lock (_lock) conns = _connections.ToArray();
            foreach (var c in conns)
            {
                var ep = c.PeerUdpEndpoint;
                if (ep == null) continue; // responder hasn't learned the peer's address yet
                try { _udp.SendTo(c.DataSession.Seal(LinkMessageType.Keepalive, 0, 0, Array.Empty<byte>()), ep); }
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

                // Both sides spray the peer's candidates AND listen; the role
                // only chooses who leads the handshake once the path is open.
                var punched = await PunchedConnection.ConnectTwoWayAsync(
                    punchAdapter, controlAdapter, sharedNonce, candidates, isInitiator,
                    _identity, _trust, expose, _caps, _approve, _nowUtc(), punchTimeout, linked.Token).ConfigureAwait(false);

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
                var ep = c.PeerUdpEndpoint;
                if (ep == null) { DiagLastError = "push: peer endpoint not learned yet"; continue; }
                try
                {
                    byte[] dg = c.DataSession.Seal(LinkMessageType.Input, slot, timestampUs, payload);
                    _udp.SendTo(dg, ep);
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
                var ep = c.PeerUdpEndpoint;
                if (ep == null) continue;
                try
                {
                    _udp.SendTo(c.DataSession.Seal(LinkMessageType.Output, slot, ts, payload), ep);
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
                var ep = c.PeerUdpEndpoint;
                // Same diag trailer as the PushOutputEffect twin (audit
                // 2026-07-25, C32): a demand dropped for an unlearned
                // endpoint was invisible in diagnostics.
                if (ep == null) { DiagLastError = "demand: peer endpoint not learned yet"; continue; }
                try
                {
                    _udp.SendTo(c.DataSession.Seal(LinkMessageType.SourceDemand, slot, ts, payload), ep);
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
                var ep = c.PeerUdpEndpoint;
                if (ep == null) continue;
                try
                {
                    _udp.SendTo(c.DataSession.Seal(LinkMessageType.Audio, slot, ts, payload), ep);
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

        private void Register(LinkConnectionResult result, TcpClient client, IPEndPoint peerUdpEndpoint, IReadOnlyList<RemotePeerDeviceInfo> exposeLocal)
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
            // first byte is (type<<4)|epoch with type 1..7, never 0xC0-0xC3.
            if (!_punchSinks.IsEmpty || !_controlSinks.IsEmpty)
            {
                byte tag0 = datagram.Length > 0 ? datagram[0] : (byte)0;
                if (tag0 == 0xC2 || tag0 == 0xC3) // punch ping/pong
                {
                    var dg = datagram.ToArray();
                    foreach (var sink in _punchSinks.Values) sink(from, dg);
                    return;
                }
                if ((tag0 == 0xC0 || tag0 == 0xC1) && datagram.Length >= 9) // control data/ack
                {
                    uint chan = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(datagram.Slice(1));
                    if (_controlSinks.TryGetValue(chan, out var csink)) { csink(datagram.ToArray()); return; }
                }
            }

            LinkPeerConnection[] conns;
            lock (_lock) conns = _connections.ToArray();

            // The AEAD tag identifies the owning session — only the right session
            // opens it. A failed open never advances a replay window.
            foreach (var c in conns)
            {
                if (!c.DataSession.Open(datagram, out var type, out byte slot, out ulong ts, out byte[] payload))
                    continue;

                System.Threading.Interlocked.Increment(ref DiagDatagramsOpened);
                System.Threading.Interlocked.Exchange(ref c.LastActivityTicks, System.Diagnostics.Stopwatch.GetTimestamp());
                // Learn the peer's UDP endpoint on first verified datagram (responder side).
                if (c.PeerUdpEndpoint == null) c.PeerUdpEndpoint = from;

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
                else if (type == LinkMessageType.DeviceList)
                {
                    // The owner's current exposed-device set: add new, remove gone, update
                    // active/inactive — so devices hot-plugged after connect appear live (#138).
                    try { ReconcileRemoteDevices(c, LinkConnection.DecodeDeviceList(payload)); }
                    catch (Exception ex) { DiagLastError = "devlist-recv: " + ex.Message; }
                }
                return;
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
                var ep = c.PeerUdpEndpoint;
                if (ep == null) continue;
                try { _udp.SendTo(c.DataSession.Seal(LinkMessageType.DeviceList, 0, ts, payload), ep); }
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
            public TcpClient Tcp;
            public string PeerFingerprintHex;
            public long LastActivityTicks; // QPC; updated on each verified datagram, read by the reaper
        }
    }
}
