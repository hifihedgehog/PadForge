using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace PadForge.Engine.RemoteLink
{
    /// <summary>A PadForge instance found on the local network (issue #138).</summary>
    public sealed class DiscoveredPeer
    {
        public string Name { get; init; }
        public IPEndPoint Endpoint { get; init; }
        public string FingerprintHex { get; init; }
        public long LastSeenTicks { get; set; }
    }

    /// <summary>
    /// Zero-config LAN discovery so a layman never types an IP: each instance
    /// announces itself with a small UDP broadcast (its name, link port, and
    /// identity fingerprint), and listens for others. Discovered PCs surface in a
    /// "Nearby PCs" list to click. Same-subnet only; cross-network uses a code.
    ///
    /// A deliberately simple PadForge-to-PadForge beacon, not full mDNS — no
    /// dependency, and the connection itself is still gated by the crypto pairing,
    /// so a forged beacon only puts a name in a list, it can't admit anyone.
    /// </summary>
    public sealed class LinkDiscovery : IDisposable
    {
        public const int DiscoveryPort = 27501;
        private const int AnnounceIntervalMs = 2000;
        private const long StaleAfterMs = 10000;
        private static readonly byte[] Magic = { (byte)'P', (byte)'F', (byte)'L', (byte)'K' };
        private const byte Version = 1;

        private readonly object _lock = new();
        private readonly Dictionary<string, DiscoveredPeer> _peers = new(); // keyed by fingerprint hex
        private readonly Func<long> _nowTicks;
        private readonly long _staleTicks;

        private Socket _socket;
        private Timer _announceTimer;
        private CancellationTokenSource _cts;
        private byte[] _beacon;
        private string _ownFingerprintHex;

        /// <summary>Raised (on a background thread) when the peer list changes.</summary>
        public event Action PeersChanged;

        public LinkDiscovery(Func<long> nowTicksProvider = null)
        {
            _nowTicks = nowTicksProvider ?? System.Diagnostics.Stopwatch.GetTimestamp;
            _staleTicks = System.Diagnostics.Stopwatch.Frequency * StaleAfterMs / 1000;
        }

        public bool IsRunning { get; private set; }

        public void Start(int linkPort, string displayName, string fingerprintHex)
        {
            if (IsRunning) return;
            _ownFingerprintHex = fingerprintHex ?? "";
            _beacon = BuildBeacon(linkPort, displayName, fingerprintHex);
            _cts = new CancellationTokenSource();

            try
            {
                _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _socket.EnableBroadcast = true;
                try { _socket.IOControl(unchecked((int)0x9800000C) /* SIO_UDP_CONNRESET */, new byte[4], null); } catch { }
                _socket.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
            }
            catch
            {
                // Bind can fail, most obviously when the discovery port is
                // already held. IsRunning is only set below, and both Stop and
                // Dispose gate on it, so a failure here used to strand the
                // socket and the CancellationTokenSource with no reachable
                // path to release them: every retry leaked another pair.
                // Clean up here, then rethrow so the caller still sees the
                // failure rather than a silently dead discovery service.
                try { _socket?.Dispose(); } catch { /* best effort */ }
                _socket = null;
                try { _cts?.Dispose(); } catch { /* best effort */ }
                _cts = null;
                throw;
            }

            IsRunning = true;
            _ = ReceiveLoopAsync(_cts.Token);
            _announceTimer = new Timer(_ => Announce(), null, 0, AnnounceIntervalMs);
        }

        public void Stop()
        {
            if (!IsRunning) return;
            IsRunning = false;
            try { _cts.Cancel(); } catch { }
            _announceTimer?.Dispose(); _announceTimer = null;
            try { _socket?.Close(); } catch { }
            _cts?.Dispose();
            lock (_lock) _peers.Clear();
        }

        /// <summary>Currently-visible peers (prunes stale ones).</summary>
        public IReadOnlyList<DiscoveredPeer> Peers
        {
            get
            {
                long now = _nowTicks();
                lock (_lock)
                {
                    var stale = _peers.Where(kv => now - kv.Value.LastSeenTicks > _staleTicks).Select(kv => kv.Key).ToList();
                    foreach (var k in stale) _peers.Remove(k);
                    return _peers.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
                }
            }
        }

        // Cap the discovered-peer table so a LAN beacon flood (rotating fingerprints) can't
        // grow it without bound; known peers keep refreshing and are never evicted by the cap (#138 F24).
        private const int MaxPeers = 256;

        private void Announce()
        {
            try { _socket?.SendTo(_beacon, new IPEndPoint(IPAddress.Broadcast, DiscoveryPort)); }
            catch { /* interface down / no network — silent */ }
        }

        private async System.Threading.Tasks.Task ReceiveLoopAsync(CancellationToken ct)
        {
            var buf = new byte[512];
            var any = new IPEndPoint(IPAddress.Any, 0);
            while (!ct.IsCancellationRequested)
            {
                SocketReceiveFromResult r;
                try { r = await _socket.ReceiveFromAsync(buf, SocketFlags.None, any, ct); }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch { continue; }

                if (!TryParseBeacon(buf.AsSpan(0, r.ReceivedBytes), out int linkPort, out string name, out string fpHex))
                    continue;
                if (fpHex == _ownFingerprintHex) continue; // ignore our own announce

                var from = (IPEndPoint)r.RemoteEndPoint;
                var endpoint = new IPEndPoint(from.Address, linkPort);
                bool changed = false;
                lock (_lock)
                {
                    if (_peers.TryGetValue(fpHex, out var existing))
                    {
                        existing.LastSeenTicks = _nowTicks();
                        changed = existing.Name != name || !existing.Endpoint.Equals(endpoint);
                        if (changed) _peers[fpHex] = new DiscoveredPeer { Name = name, Endpoint = endpoint, FingerprintHex = fpHex, LastSeenTicks = _nowTicks() };
                    }
                    else if (_peers.Count < MaxPeers)
                    {
                        _peers[fpHex] = new DiscoveredPeer { Name = name, Endpoint = endpoint, FingerprintHex = fpHex, LastSeenTicks = _nowTicks() };
                        changed = true;
                    }
                }
                if (changed) PeersChanged?.Invoke();
            }
        }

        public void Dispose() => Stop();

        // ── Beacon codec (testable) ─────────────────────────────────────────

        public static byte[] BuildBeacon(int linkPort, string displayName, string fingerprintHex)
        {
            byte[] name = Encoding.UTF8.GetBytes(displayName ?? "");
            if (name.Length > 64) name = name.AsSpan(0, 64).ToArray();
            byte[] fp = (fingerprintHex ?? "").Length > 0 ? Encoding.ASCII.GetBytes(fingerprintHex) : Array.Empty<byte>();
            if (fp.Length > 64) fp = fp.AsSpan(0, 64).ToArray();

            var buf = new byte[4 + 1 + 2 + 1 + fp.Length + 1 + name.Length];
            int o = 0;
            Magic.CopyTo(buf.AsSpan(o)); o += 4;
            buf[o++] = Version;
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(o), (ushort)linkPort); o += 2;
            buf[o++] = (byte)fp.Length;
            fp.CopyTo(buf.AsSpan(o)); o += fp.Length;
            buf[o++] = (byte)name.Length;
            name.CopyTo(buf.AsSpan(o));
            return buf;
        }

        public static bool TryParseBeacon(ReadOnlySpan<byte> data, out int linkPort, out string displayName, out string fingerprintHex)
        {
            linkPort = 0; displayName = ""; fingerprintHex = "";
            try
            {
                if (data.Length < 8) return false;
                if (!data.Slice(0, 4).SequenceEqual(Magic)) return false;
                if (data[4] != Version) return false;
                int o = 5;
                linkPort = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(o, 2)); o += 2;
                // Accept only what the builder emits (64-byte caps): a hostile
                // beacon must not plant a 255-byte name in the peers UI list.
                int fpLen = data[o++];
                if (fpLen > 64 || o + fpLen > data.Length) return false;
                fingerprintHex = Encoding.ASCII.GetString(data.Slice(o, fpLen)); o += fpLen;
                int nameLen = data[o++];
                if (nameLen > 64 || o + nameLen > data.Length) return false;
                displayName = Encoding.UTF8.GetString(data.Slice(o, nameLen));
                return true;
            }
            catch { return false; }
        }
    }
}
