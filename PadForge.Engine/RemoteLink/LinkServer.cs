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
        public int DiagAudioReceived;   // speaker PCM blocks we opened+surfaced (#138)
        public string DiagLastError;

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
                ExposedLocal = exposeLocal?.ToArray() ?? Array.Empty<RemotePeerDeviceInfo>(),
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
            public RemotePeerDeviceInfo[] ExposedLocal;
            public long LastActivityTicks; // QPC; updated on each verified datagram, read by the reaper
        }
    }
}
