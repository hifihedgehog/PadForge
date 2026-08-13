using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace PadForge.Engine.RemoteLink.Dht
{
    /// <summary>
    /// The live <see cref="IKrpcTransport"/> over a dedicated UDP socket for DHT
    /// presence traffic (#294). This is NOT the Remote Link data socket: DHT and
    /// the sealed data plane are separate concerns, and a dedicated socket keeps
    /// the classifiable-as-BitTorrent traffic isolated and easy to disable when
    /// the internet lane is off. IPv4, matching the mainline compact-node format
    /// the store parses.
    /// </summary>
    public sealed class UdpKrpcTransport : IKrpcTransport, IDisposable
    {
        private const int SIO_UDP_CONNRESET = unchecked((int)0x9800000C);
        private readonly Socket _socket;
        private readonly CancellationTokenSource _cts = new();
        private volatile bool _disposed;
        public Action<IPEndPoint, byte[]> OnDatagram { get; set; }

        public UdpKrpcTransport()
        {
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try { _socket.IOControl(SIO_UDP_CONNRESET, new byte[4], null); } catch { /* non-Windows */ }
            _socket.Bind(new IPEndPoint(IPAddress.Any, 0)); // ephemeral port
            _ = ReceiveLoopAsync(_cts.Token);
        }

        public async Task SendAsync(byte[] datagram, IPEndPoint node, CancellationToken ct)
        {
            if (_disposed) return;
            try { await _socket.SendToAsync(datagram, SocketFlags.None, node, ct).ConfigureAwait(false); }
            catch (ObjectDisposedException) { }
            catch (SocketException) { /* unreachable host: the RPC simply times out */ }
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            var buf = new byte[2048];
            var any = new IPEndPoint(IPAddress.Any, 0);
            while (!ct.IsCancellationRequested)
            {
                SocketReceiveFromResult r;
                try { r = await _socket.ReceiveFromAsync(buf, SocketFlags.None, any, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { continue; }
                var dg = new byte[r.ReceivedBytes];
                Array.Copy(buf, dg, r.ReceivedBytes);
                // Guarded: this loop is fire-and-forget, so a throw out of the
                // handler killed the DHT's only reader with nothing to observe
                // the failure, and every later lookup silently timed out.
                try { OnDatagram?.Invoke((IPEndPoint)r.RemoteEndPoint, dg); }
                catch (Exception ex) { SdlDiagLog.WriteLine("DHT datagram handler threw: " + ex.Message); }
            }
        }

        /// <summary>Resolves the default bootstrap routers to IPv4 endpoints.
        /// Unresolvable entries (DNS blocked) are skipped; an empty result means
        /// the DHT is unreachable from this network, which the caller reports.</summary>
        public static async Task<System.Collections.Generic.List<IPEndPoint>> ResolveBootstrapAsync(CancellationToken ct)
        {
            var list = new System.Collections.Generic.List<IPEndPoint>();
            foreach (var (host, port) in DhtPresenceStore.DefaultBootstrap)
            {
                try
                {
                    var addrs = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct).ConfigureAwait(false);
                    if (addrs.Length > 0) list.Add(new IPEndPoint(addrs[0], port));
                }
                catch { /* skip unresolvable */ }
            }
            return list;
        }

        public void Dispose()
        {
            _disposed = true;
            try { _cts.Cancel(); } catch { }
            try { _socket.Dispose(); } catch { }
            try { _cts.Dispose(); } catch { }
        }
    }
}
