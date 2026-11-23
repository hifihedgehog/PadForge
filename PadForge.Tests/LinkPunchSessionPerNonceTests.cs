using System;
using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Engine.RemoteLink;
using PadForge.Engine.RemoteLink.Dht;

namespace PadForge.Tests
{
    public partial class LinkServerTests
    {
        /// <summary>Two paired PCs list each other, so each answers the
        /// other's reconnect dial. The PC whose maintenance pass dials also
        /// heard the answering PC's probes, which carry the pass's own nonce,
        /// and started a second session of its own on that nonce and channel.
        /// The second session took the channel's control datagrams from the
        /// pass, so the pass never finished its handshake. One PC dials and
        /// the other is idle, over real loopback sockets, with the dialing PC
        /// leading the handshake and with it following.</summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task APairedDial_RunsOneSessionOnTheDialingPCAndConnects(bool dialerLeads)
        {
            var first = PeerIdentity.Generate();
            var second = PeerIdentity.Generate();
            var lower = RemoteLinkInternetService.LeadsHandshake(first.Fingerprint, second.Fingerprint) ? first : second;
            var higher = ReferenceEquals(lower, first) ? second : first;
            var dialing = dialerLeads ? lower : higher;
            var answering = dialerLeads ? higher : lower;
            var cap = new byte[32];
            new Random(7).NextBytes(cap);
            byte[] nonce = PresenceRecord.PunchNonce(cap);

            using var dialer = new LinkServer(dialing, new PeerTrustStore(), _ => true);
            using var host = new LinkServer(answering, new PeerTrustStore(), _ => true);
            int hostPort = StartOnFreePort(host);
            StartOnFreePort(dialer, hostPort);
            // What each PC's maintenance pass hands its listener: every paired
            // peer, the one it dials included.
            dialer.SetKnownPunchPeers(new[] { (answering.PublicKey, cap) });
            host.SetKnownPunchPeers(new[] { (dialing.PublicKey, cap) });

            const BindingFlags fields = BindingFlags.NonPublic | BindingFlags.Instance;
            var dialerAuto = (ConcurrentDictionary<string, byte>)typeof(LinkServer)
                .GetField("_autoResponding", fields).GetValue(dialer);
            int dialerAnswered = 0;
            using var stopWatching = new CancellationTokenSource();
            var watch = Task.Factory.StartNew(() =>
            {
                while (!stopWatching.IsCancellationRequested)
                    if (!dialerAuto.IsEmpty) Interlocked.Exchange(ref dialerAnswered, 1);
            }, TaskCreationOptions.LongRunning);
            try
            {
                var pass = dialer.ConnectByPunchAsync(new[] { new IPEndPoint(IPAddress.Loopback, hostPort) },
                    nonce, handshakeAsInitiator: dialerLeads, Array.Empty<RemotePeerDeviceInfo>(),
                    punchTimeout: TimeSpan.FromSeconds(5));
                bool connected;
                try { connected = await pass.WaitAsync(TimeSpan.FromSeconds(15)); }
                catch (TimeoutException) { connected = false; }
                Assert.True(connected, "the pass's session never finished its handshake. dialer: "
                    + dialer.DiagLastError + " host: " + host.DiagLastError);
                Assert.Equal(0, Volatile.Read(ref dialerAnswered));
                Assert.True(await WaitUntil(() => host.HasConnections, 5000), "the host never registered the dialer");
            }
            finally
            {
                stopWatching.Cancel();
                await watch;
                dialer.Stop();
                host.Stop();
            }
        }
    }
}
