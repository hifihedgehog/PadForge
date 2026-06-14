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

        public event Action<RemotePeerDevice> DeviceConnected;
        public event Action<RemotePeerDevice> DeviceDisconnected;
        public event Action<string> StatusChanged;

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

        // Diagnostics (test/telemetry): datagrams the UDP loop saw vs successfully opened.
        public int DiagDatagramsReceived;
        public int DiagDatagramsOpened;
        public int DiagDatagramsSent;
        public string DiagLastError;

        public void Start(int port)
        {
            if (IsRunning) return;
            _port = port;
            _cts = new CancellationTokenSource();

            _tcp = new TcpListener(IPAddress.Any, port);
            _tcp.Start();

            _udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try { _udp.IOControl(SIO_UDP_CONNRESET, new byte[4], null); } catch { /* non-Windows */ }
            _udp.Bind(new IPEndPoint(IPAddress.Any, port));

            IsRunning = true;
            _ = AcceptLoopAsync(_cts.Token);
            _ = UdpLoopAsync(_cts.Token);
            StatusChanged?.Invoke($"Listening on {port}");
        }

        public void Stop()
        {
            if (!IsRunning) return;
            IsRunning = false;
            try { _cts.Cancel(); } catch { }
            LinkPeerConnection[] conns;
            lock (_lock) { conns = _connections.ToArray(); _connections.Clear(); }
            foreach (var c in conns) DropConnection(c);
            try { _tcp?.Stop(); } catch { }
            try { _udp?.Close(); } catch { }
            _cts?.Dispose();
            StatusChanged?.Invoke("Stopped");
        }

        /// <summary>Initiate an outbound pairing/reconnect to a peer, optionally exposing local devices.</summary>
        public async Task<bool> ConnectAsync(string host, int port, IReadOnlyList<RemotePeerDeviceInfo> exposeLocal, CancellationToken ct = default)
        {
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts?.Token ?? CancellationToken.None);
                // IPv4 to match the IPv4 UDP data socket (the DsuMotionServer model);
                // a default dual-stack TcpClient yields IPv4-mapped-IPv6 endpoints the
                // IPv4 UDP socket can't SendTo.
                var client = new TcpClient(AddressFamily.InterNetwork);
                await client.ConnectAsync(host, port, linked.Token);
                var channel = new TcpControlChannel(client.GetStream());
                var result = await LinkConnection.RunInitiatorAsync(channel, _identity, _trust, exposeLocal, _caps, _approve, _nowUtc(), linked.Token);

                var peerIp = ((IPEndPoint)client.Client.RemoteEndPoint).Address;
                if (peerIp.IsIPv4MappedToIPv6) peerIp = peerIp.MapToIPv4();
                Register(result, client, new IPEndPoint(peerIp, port), exposeLocal);
                return true;
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"Connect failed: {ex.Message}");
                return false;
            }
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
            byte[] payload = CustomInputStateCodec.Encode(state, caps);
            LinkPeerConnection[] conns;
            lock (_lock) conns = _connections.ToArray();
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

        // ── Accept / handshake (responder) ──────────────────────────────────

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _tcp.AcceptTcpClientAsync(ct); }
                catch { break; }
                _ = HandleResponderAsync(client, ct);
            }
        }

        private async Task HandleResponderAsync(TcpClient client, CancellationToken ct)
        {
            try
            {
                var channel = new TcpControlChannel(client.GetStream());
                var result = await LinkConnection.RunResponderAsync(channel, _identity, _trust, Array.Empty<RemotePeerDeviceInfo>(), _caps, _approve, _nowUtc(), ct);
                // The peer's UDP endpoint is learned from the first inbound datagram.
                Register(result, client, peerUdpEndpoint: null, exposeLocal: Array.Empty<RemotePeerDeviceInfo>());
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"Link rejected: {ex.Message}");
                try { client.Dispose(); } catch { }
            }
        }

        private void Register(LinkConnectionResult result, TcpClient client, IPEndPoint peerUdpEndpoint, IReadOnlyList<RemotePeerDeviceInfo> exposeLocal)
        {
            var conn = new LinkPeerConnection
            {
                DataSession = new LinkSession(result.DataKey, result.IsInitiator),
                RemoteDevices = result.RemoteDevices.ToArray(),
                PeerUdpEndpoint = peerUdpEndpoint,
                Tcp = client,
                PeerFingerprintHex = result.PeerFingerprintHex,
                ExposedLocal = exposeLocal?.ToArray() ?? Array.Empty<RemotePeerDeviceInfo>(),
            };
            lock (_lock) _connections.Add(conn);
            foreach (var d in conn.RemoteDevices) DeviceConnected?.Invoke(d);
            StatusChanged?.Invoke($"Peer {Short(conn.PeerFingerprintHex)} connected, {conn.RemoteDevices.Length} device(s)");
        }

        // ── UDP receive (data + learn-endpoint) ─────────────────────────────

        private async Task UdpLoopAsync(CancellationToken ct)
        {
            var buf = new byte[2048];
            var any = new IPEndPoint(IPAddress.Any, 0);
            while (!ct.IsCancellationRequested)
            {
                SocketReceiveFromResult r;
                try { r = await _udp.ReceiveFromAsync(buf, SocketFlags.None, any, ct); }
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
            LinkPeerConnection[] conns;
            lock (_lock) conns = _connections.ToArray();

            // The AEAD tag identifies the owning session — only the right session
            // opens it. A failed open never advances a replay window.
            foreach (var c in conns)
            {
                if (!c.DataSession.Open(datagram, out var type, out byte slot, out _, out byte[] payload))
                    continue;

                System.Threading.Interlocked.Increment(ref DiagDatagramsOpened);
                // Learn the peer's UDP endpoint on first verified datagram (responder side).
                if (c.PeerUdpEndpoint == null) c.PeerUdpEndpoint = from;

                if (type == LinkMessageType.Input)
                {
                    var dev = c.RemoteDevices.FirstOrDefault();
                    dev?.ApplyFramePayload(payload);
                }
                else if (type == LinkMessageType.Haptic)
                {
                    // Reserved for the feedback return path (replays onto the local device).
                }
                return;
            }
        }

        private void DropConnection(LinkPeerConnection c)
        {
            foreach (var d in c.RemoteDevices)
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
            public RemotePeerDevice[] RemoteDevices;
            public volatile IPEndPoint PeerUdpEndpoint;
            public TcpClient Tcp;
            public string PeerFingerprintHex;
            public RemotePeerDeviceInfo[] ExposedLocal;
        }
    }
}
